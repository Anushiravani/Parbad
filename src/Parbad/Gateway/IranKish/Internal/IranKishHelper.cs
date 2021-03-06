// Copyright (c) Parbad. All rights reserved.
// Licensed under the GNU GENERAL PUBLIC License, Version 3.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Parbad.Abstraction;
using Parbad.Http;
using Parbad.Internal;
using Parbad.Options;
using Parbad.Utilities;

namespace Parbad.Gateway.IranKish.Internal
{
    internal static class IranKishHelper
    {
        public const string PaymentPageUrl = "https://ikc.shaparak.ir/TPayment/Payment/index";
        public const string BaseServiceUrl = "https://ikc.shaparak.ir/";
        public const string TokenWebServiceUrl = "/TToken/Tokens.svc";
        public const string VerifyWebServiceUrl = "/TVerify/Verify.svc";

        public static KeyValuePair<string, string> HttpRequestHeader => new KeyValuePair<string, string>("SOAPAction", "http://tempuri.org/ITokens/MakeToken");
        public static KeyValuePair<string, string> HttpVerifyHeader => new KeyValuePair<string, string>("SOAPAction", "http://tempuri.org/IVerify/KicccPaymentsVerification");

        private const string OkResult = "100";

        public static string CreateRequestData(Invoice invoice, IranKishGatewayAccount account)
        {
            return
                "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:tem=\"http://tempuri.org/\">" +
                "<soapenv:Header/>" +
                "<soapenv:Body>" +
                "<tem:MakeToken>" +
                $"<tem:amount>{(long)invoice.Amount}</tem:amount>" +
                $"<tem:merchantId>{account.MerchantId}</tem:merchantId>" +
                $"<tem:invoiceNo>{invoice.TrackingNumber}</tem:invoiceNo>" +
                "<tem:paymentId></tem:paymentId>" +
                "<tem:specialPaymentId></tem:specialPaymentId>" +
                $"<tem:revertURL>{XmlHelper.EncodeXmlValue(invoice.CallbackUrl)}</tem:revertURL>" +
                "<tem:description></tem:description>" +
                "</tem:MakeToken>" +
                "</soapenv:Body>" +
                "</soapenv:Envelope>";
        }

        public static PaymentRequestResult CreateRequestResult(
            string webServiceResponse,
            Invoice invoice,
            IranKishGatewayAccount account,
            HttpContext httpContext,
            MessagesOptions messagesOptions)
        {
            var result = XmlHelper.GetNodeValueFromXml(webServiceResponse, "result", "http://schemas.datacontract.org/2004/07/Token");
            var message = XmlHelper.GetNodeValueFromXml(webServiceResponse, "message", "http://schemas.datacontract.org/2004/07/Token");
            var token = XmlHelper.GetNodeValueFromXml(webServiceResponse, "token", "http://schemas.datacontract.org/2004/07/Token");

            var isSucceed = result.Equals("true", StringComparison.OrdinalIgnoreCase) && !token.IsNullOrEmpty();

            if (!isSucceed)
            {
                if (message.IsNullOrEmpty())
                {
                    message = messagesOptions.InvalidDataReceivedFromGateway;
                }

                return PaymentRequestResult.Failed(message, account.Name);
            }

            var transporterDescriptor = GatewayTransporterDescriptor.CreatePost(
                PaymentPageUrl,
                new Dictionary<string, string>
                {
                    {"merchantid", account.MerchantId},
                    {"token", token}
                });

            var transporter = new DefaultGatewayTransporter(httpContext, transporterDescriptor);

            return PaymentRequestResult.Succeed(transporter, account.Name);
        }

        public static async Task<IranKishCallbackResult> CreateCallbackResultAsync(InvoiceContext context,
            IranKishGatewayAccount account,
            HttpRequest httpRequest,
            MessagesOptions messagesOptions,
            CancellationToken cancellationToken)
        {
            var resultCode = await httpRequest.TryGetParamAsync("ResultCode", cancellationToken).ConfigureAwaitFalse();
            var token = await httpRequest.TryGetParamAsync("Token", cancellationToken).ConfigureAwaitFalse();
            var merchantId = await httpRequest.TryGetParamAsync("MerchantId", cancellationToken).ConfigureAwaitFalse();

            // Equals to TrackingNumber in Parbad system.
            var invoiceNumber = await httpRequest.TryGetParamAsAsync<long>("InvoiceNumber", cancellationToken).ConfigureAwaitFalse();

            // Equals to TransactionCode in Parbad system.
            var referenceId = await httpRequest.TryGetParamAsync("ReferenceId", cancellationToken).ConfigureAwaitFalse();

            var isSucceed = false;
            PaymentVerifyResult verifyResult = null;

            if (merchantId.Value != account.MerchantId ||
                invoiceNumber.Value != context.Payment.TrackingNumber ||
                !token.Exists ||
                token.Value.IsNullOrEmpty())
            {
                verifyResult = new PaymentVerifyResult
                {
                    TrackingNumber = invoiceNumber.Value,
                    TransactionCode = referenceId.Value,
                    IsSucceed = false,
                    Message = messagesOptions.InvalidDataReceivedFromGateway
                };
            }
            else
            {
                var translatedMessage = IranKishGatewayResultTranslator.Translate(resultCode.Value, messagesOptions);

                isSucceed = resultCode.Value == OkResult;

                if (!isSucceed)
                {
                    verifyResult = new PaymentVerifyResult
                    {
                        TrackingNumber = invoiceNumber.Value,
                        TransactionCode = referenceId.Value,
                        Status = PaymentVerifyResultStatus.Failed,
                        Message = translatedMessage
                    };
                }
            }

            return new IranKishCallbackResult
            {
                IsSucceed = isSucceed,
                Token = token.Value,
                InvoiceNumber = invoiceNumber.Value,
                ReferenceId = referenceId.Value,
                Result = verifyResult
            };
        }

        public static string CreateVerifyData(IranKishCallbackResult callbackResult, IranKishGatewayAccount account)
        {
            return
                "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:tem=\"http://tempuri.org/\">" +
                "<soapenv:Header/>" +
                "<soapenv:Body>" +
                "<tem:KicccPaymentsVerification>" +
                $"<tem:token>{XmlHelper.EncodeXmlValue(callbackResult.Token)}</tem:token>" +
                $"<tem:merchantId>{account.MerchantId}</tem:merchantId>" +
                $"<tem:referenceNumber>{callbackResult.ReferenceId}</tem:referenceNumber>" +
                "<tem:sha1Key></tem:sha1Key>" +
                "</tem:KicccPaymentsVerification>" +
                "</soapenv:Body>" +
                "</soapenv:Envelope>";
        }

        public static PaymentVerifyResult CreateVerifyResult(
            string webServiceResponse,
            InvoiceContext context,
            IranKishCallbackResult callbackResult,
            MessagesOptions messagesOptions)
        {
            var result = XmlHelper.GetNodeValueFromXml(webServiceResponse, "KicccPaymentsVerificationResult");

            // The result object is actually the amount of invoice . It must equal to invoice's amount.
            if (!long.TryParse(result, out var numericResult))
            {
                return new PaymentVerifyResult
                {
                    TrackingNumber = callbackResult.InvoiceNumber,
                    TransactionCode = callbackResult.ReferenceId,
                    Status = PaymentVerifyResultStatus.Failed,
                    Message = messagesOptions.InvalidDataReceivedFromGateway
                };
            }

            var isSuccess = numericResult != (long)context.Payment.Amount;

            var translatedMessage = isSuccess
                ? messagesOptions.PaymentSucceed
                : IranKishGatewayResultTranslator.Translate(result, messagesOptions);

            return new PaymentVerifyResult
            {
                TrackingNumber = callbackResult.InvoiceNumber,
                TransactionCode = callbackResult.ReferenceId,
                Status = PaymentVerifyResultStatus.Succeed,
                Message = translatedMessage
            };
        }
    }
}
