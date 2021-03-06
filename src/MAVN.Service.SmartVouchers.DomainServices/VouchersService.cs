﻿using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Common.Log;
using Lykke.Common.Log;
using Lykke.RabbitMqBroker.Publisher;
using MAVN.Service.PaymentManagement.Client;
using MAVN.Service.PaymentManagement.Client.Models.Requests;
using MAVN.Service.PaymentManagement.Client.Models.Responses;
using MAVN.Service.SmartVouchers.Contract;
using MAVN.Service.SmartVouchers.Domain.Enums;
using MAVN.Service.SmartVouchers.Domain.Models;
using MAVN.Service.SmartVouchers.Domain.Repositories;
using MAVN.Service.SmartVouchers.Domain.Services;

namespace MAVN.Service.SmartVouchers.DomainServices
{
    public class VouchersService : IVouchersService
    {
        private const int MaxAttemptsCount = 5;

        private readonly IPaymentManagementClient _paymentManagementClient;
        private readonly IVouchersRepository _vouchersRepository;
        private readonly ICampaignsRepository _campaignsRepository;
        private readonly IPaymentRequestsRepository _paymentRequestsRepository;
        private readonly IRedisLocksService _redisLocksService;
        private readonly IRabbitPublisher<SmartVoucherSoldEvent> _voucherSoldPublisher;
        private readonly ILog _log;
        private readonly TimeSpan _lockTimeOut;

        public VouchersService(
            IPaymentManagementClient paymentManagementClient,
            IVouchersRepository vouchersRepository,
            ICampaignsRepository campaignsRepository,
            IPaymentRequestsRepository paymentRequestsRepository,
            ILogFactory logFactory,
            IRedisLocksService redisLocksService,
            IRabbitPublisher<SmartVoucherSoldEvent> voucherSoldPublisher,
            TimeSpan lockTimeOut)
        {
            _paymentManagementClient = paymentManagementClient;
            _vouchersRepository = vouchersRepository;
            _campaignsRepository = campaignsRepository;
            _paymentRequestsRepository = paymentRequestsRepository;
            _redisLocksService = redisLocksService;
            _voucherSoldPublisher = voucherSoldPublisher;
            _log = logFactory.CreateLog(this);
            _lockTimeOut = lockTimeOut;
        }

        public async Task<ProcessingVoucherError> ProcessPaymentRequestAsync(Guid paymentRequestId)
        {
            var voucherShortCode = await _paymentRequestsRepository.GetVoucherShortCodeAsync(paymentRequestId);

            var voucher = await _vouchersRepository.GetByShortCodeAsync(voucherShortCode);
            if (voucher == null)
                return ProcessingVoucherError.VoucherNotFound;

            if (voucher.OwnerId == null)
            {
                _log.Error(message: "Reserved voucher with missing owner", context: voucher);
                throw new InvalidOperationException("Reserved voucher with missing owner");
            }

            var voucherCampaign = await _campaignsRepository.GetByIdAsync(voucher.CampaignId, false);
            if (voucherCampaign == null)
                return ProcessingVoucherError.VoucherCampaignNotFound;

            voucher.Status = VoucherStatus.Sold;
            await _vouchersRepository.UpdateAsync(voucher);

            await _voucherSoldPublisher.PublishAsync(new SmartVoucherSoldEvent
            {
                Amount = voucherCampaign.VoucherPrice,
                Currency = voucherCampaign.Currency,
                CustomerId = voucher.OwnerId.Value,
                PartnerId = voucherCampaign.PartnerId,
                Timestamp = DateTime.UtcNow,
                CampaignId = voucher.CampaignId,
                VoucherShortCode = voucher.ShortCode,
                PaymentRequestId = paymentRequestId.ToString(),
            });

            return ProcessingVoucherError.None;
        }

        public async Task<VoucherReservationResult> ReserveVoucherAsync(Guid voucherCampaignId, Guid ownerId)
        {
            var campaign = await _campaignsRepository.GetByIdAsync(voucherCampaignId, false);
            if (campaign == null)
                return new VoucherReservationResult { ErrorCode = ProcessingVoucherError.VoucherCampaignNotFound };

            if (campaign.State != CampaignState.Published
                || DateTime.UtcNow < campaign.FromDate
                || campaign.ToDate.HasValue && campaign.ToDate.Value < DateTime.UtcNow)
                return new VoucherReservationResult { ErrorCode = ProcessingVoucherError.VoucherCampaignNotActive };

            if (campaign.VouchersTotalCount <= campaign.BoughtVouchersCount)
                return new VoucherReservationResult { ErrorCode = ProcessingVoucherError.NoAvailableVouchers };

            var voucherCampaignIdStr = voucherCampaignId.ToString();
            for (int i = 0; i < MaxAttemptsCount; ++i)
            {
                var locked = await _redisLocksService.TryAcquireLockAsync(
                    voucherCampaignIdStr,
                    ownerId.ToString(),
                    _lockTimeOut);
                if (!locked)
                {
                    await Task.Delay(_lockTimeOut);
                    continue;
                }

                var vouchers = await _vouchersRepository.GetByCampaignIdAndStatusAsync(voucherCampaignId, VoucherStatus.InStock);
                Voucher voucher = null;
                if (vouchers.Any())
                {
                    try
                    {
                        voucher = vouchers.FirstOrDefault();
                        await _vouchersRepository.ReserveAsync(voucher, ownerId);
                    }
                    catch (Exception e)
                    {
                        _log.Error(e);
                        await _redisLocksService.ReleaseLockAsync(voucherCampaignIdStr, ownerId.ToString());
                        return new VoucherReservationResult { ErrorCode = ProcessingVoucherError.NoAvailableVouchers };
                    }
                }
                else
                {
                    var vouchersPage = await _vouchersRepository.GetByCampaignIdAsync(voucherCampaignId, 0, 1);
                    if (vouchersPage.TotalCount >= campaign.VouchersTotalCount)
                    {
                        await _redisLocksService.ReleaseLockAsync(voucherCampaignIdStr, ownerId.ToString());
                        return new VoucherReservationResult { ErrorCode = ProcessingVoucherError.NoAvailableVouchers };
                    }

                    var (validationCode, hash) = GenerateValidation();
                    voucher = new Voucher
                    {
                        CampaignId = voucherCampaignId,
                        Status = VoucherStatus.Reserved,
                        ValidationCodeHash = hash,
                        OwnerId = ownerId,
                        PurchaseDate = DateTime.UtcNow,
                    };

                    voucher.Id = await _vouchersRepository.CreateAsync(voucher);
                    voucher.ShortCode = GenerateShortCodeFromId(voucher.Id);

                    await _vouchersRepository.UpdateAsync(voucher, validationCode);
                }

                await _redisLocksService.ReleaseLockAsync(voucherCampaignIdStr, ownerId.ToString());

                var paymentRequestResult = await _paymentManagementClient.Api.GeneratePaymentAsync(
                    new PaymentGenerationRequest
                    {
                        CustomerId = ownerId,
                        Amount = campaign.VoucherPrice,
                        Currency = campaign.Currency,
                        PartnerId = campaign.PartnerId,
                    });
                if (paymentRequestResult.ErrorCode != PaymentGenerationErrorCode.None)
                {
                    await CancelReservationAsync(voucher.ShortCode);
                    return new VoucherReservationResult
                    {
                        ErrorCode = ProcessingVoucherError.InvalidPartnerPaymentConfiguration,
                    };
                }

                await _paymentRequestsRepository.CreatePaymentRequestAsync(paymentRequestResult.PaymentRequestId, voucher.ShortCode);

                return new VoucherReservationResult
                {
                    ErrorCode = ProcessingVoucherError.None,
                    PaymentUrl = paymentRequestResult.PaymentPageUrl,
                };
            }

            _log.Warning($"Couldn't get a lock for voucher campaign {voucherCampaignId}");

            return new VoucherReservationResult { ErrorCode = ProcessingVoucherError.NoAvailableVouchers };
        }

        public async Task<ProcessingVoucherError> CancelVoucherReservationAsync(string shortCode)
        {
            var voucher = await _vouchersRepository.GetByShortCodeAsync(shortCode);
            if (voucher == null)
                return ProcessingVoucherError.VoucherNotFound;

            if (voucher.Status != VoucherStatus.Reserved)
                return ProcessingVoucherError.VoucherNotFound;

            var result = await CancelReservationAsync(shortCode);
            if (result != null)
                return result.Value;

            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(_lockTimeOut);

                    var result = await CancelReservationAsync(shortCode);
                    if (result != null)
                        break;
                }
            });

            return ProcessingVoucherError.None;
        }

        public async Task<RedeemVoucherError> RedeemVoucherAsync(string voucherShortCode, string validationCode)
        {
            var voucher = await _vouchersRepository.GetWithValidationByShortCodeAsync(voucherShortCode);
            if (voucher == null)
                return RedeemVoucherError.VoucherNotFound;

            var campaign = await _campaignsRepository.GetByIdAsync(voucher.CampaignId, false);
            if (campaign == null)
                return RedeemVoucherError.VoucherCampaignNotFound;

            if (campaign.State != CampaignState.Published
                || DateTime.UtcNow < campaign.FromDate
                || campaign.ToDate.HasValue && campaign.ToDate.Value < DateTime.UtcNow)
                return RedeemVoucherError.VoucherCampaignNotActive;

            if (voucher.ValidationCode != validationCode)
                return RedeemVoucherError.WrongValidationCode;

            voucher.Status = VoucherStatus.Used;
            voucher.RedemptionDate = DateTime.UtcNow;

            await _vouchersRepository.UpdateAsync(voucher);

            return RedeemVoucherError.None;
        }

        public async Task<TransferVoucherError> TransferVoucherAsync(
            string voucherShortCode,
            Guid oldOwnerId,
            Guid newOwnerId)
        {
            var voucher = await _vouchersRepository.GetByShortCodeAsync(voucherShortCode);
            if (voucher == null)
                return TransferVoucherError.VoucherNotFound;

            if (voucher.Status != VoucherStatus.InStock)
                return TransferVoucherError.VoucherIsUsed;
            if (voucher.OwnerId != oldOwnerId)
                return TransferVoucherError.NotAnOwner;

            voucher.OwnerId = newOwnerId;
            var (code, hash) = GenerateValidation();
            voucher.ValidationCodeHash = hash;

            await _vouchersRepository.UpdateAsync(voucher, code);

            return TransferVoucherError.None;
        }

        public Task<VoucherWithValidation> GetByShortCodeAsync(string voucherShortCode)
        {
            return _vouchersRepository.GetWithValidationByShortCodeAsync(voucherShortCode);
        }

        public Task<VouchersPage> GetCampaignVouchersAsync(Guid campaignId, PageInfo pageInfo)
        {
            return _vouchersRepository.GetByCampaignIdAsync(
                campaignId,
                (pageInfo.CurrentPage - 1) * pageInfo.PageSize,
                pageInfo.PageSize);
        }

        public Task<VouchersPage> GetCustomerVouchersAsync(Guid customerId, PageInfo pageInfo)
        {
            return _vouchersRepository.GetByOwnerIdAsync(
                customerId,
                (pageInfo.CurrentPage - 1) * pageInfo.PageSize,
                pageInfo.PageSize);
        }

        private string GenerateShortCodeFromId(long voucherId)
        {
            var bytes = BitConverter.GetBytes(voucherId);
            return Base32Helper.Encode(bytes);
        }

        private (string, string) GenerateValidation()
        {
            var bytes = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString());
            var validationCode = Base32Helper.Encode(bytes);

            var cryptoTransformSha1 = SHA1.Create();
            var sha1 = cryptoTransformSha1.ComputeHash(Encoding.ASCII.GetBytes(validationCode));
            var codeHash = Convert.ToBase64String(sha1);

            return (validationCode, codeHash);
        }

        private async Task<ProcessingVoucherError?> CancelReservationAsync(string shortCode)
        {
            var locked = await _redisLocksService.TryAcquireLockAsync(shortCode, shortCode, _lockTimeOut);
            if (!locked)
                return null;

            var voucher = await _vouchersRepository.GetByShortCodeAsync(shortCode);
            if (voucher.Status != VoucherStatus.Reserved)
            {
                await _redisLocksService.ReleaseLockAsync(voucher.ShortCode, voucher.ShortCode);
                return ProcessingVoucherError.VoucherNotFound;
            }

            await _vouchersRepository.CancelReservationAsync(voucher);

            await _redisLocksService.ReleaseLockAsync(voucher.ShortCode, voucher.ShortCode);
            return ProcessingVoucherError.None;
        }
    }
}
