﻿using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Certera.Data.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Diagnostics;
using Certera.Web.Services.Dns;
using Certera.Core.Helpers;
using System.Text;

namespace Certera.Web.AcmeProviders
{
    public class CertesAcmeProvider
    {
        private const int PROCESS_WAIT_MS = 60000;
        private AcmeContext _acmeContext;
        private Data.Models.AcmeCertificate _acmeCertificate;
        private IOrderContext _order;
        private List<AuthChallengeContainer> _authChallengeContainers;
        private AcmeOrder _acmeOrder;
        private readonly ILogger<CertesAcmeProvider> _logger;
        private readonly DnsVerifier _dnsVerifier;

        public CertesAcmeProvider(ILogger<CertesAcmeProvider> logger, DnsVerifier dnsVerifier)
        {
            _logger = logger;
            _dnsVerifier = dnsVerifier;
        }

        public async Task<bool> AccountExists(string key, bool staging)
        {
            try
            {
                IKey accountKey = KeyFactory.FromPem(key);

                var acmeContext = new AcmeContext(staging
                    ? WellKnownServers.LetsEncryptStagingV2
                    : WellKnownServers.LetsEncryptV2, accountKey);

                var account = await acmeContext.Account();
                return account != null;
            }
            catch { }
            return false;
        }

        public string NewKey(KeyAlgorithm keyAlgorithm)
        {
            return KeyFactory.NewKey(keyAlgorithm).ToPem();
        }

        public async Task CreateAccount(string email, string key, bool staging)
        {
            var accountKey = KeyFactory.FromPem(key);

            var acmeContext = new AcmeContext(staging
                ? WellKnownServers.LetsEncryptStagingV2
                : WellKnownServers.LetsEncryptV2, accountKey);

            await acmeContext.NewAccount(email, true);
        }

        public async Task<string> CreateNewAccount(string email, bool staging)
        {
            var acmeContext = new AcmeContext(staging
                ? WellKnownServers.LetsEncryptStagingV2
                : WellKnownServers.LetsEncryptV2);

            await acmeContext.NewAccount(email, true);

            var keyPem = acmeContext.AccountKey.ToPem();
            return keyPem;
        }

        public void Initialize(Data.Models.AcmeCertificate acmeCert)
        {
            _acmeCertificate = acmeCert;

            IKey accountKey = KeyFactory.FromPem(acmeCert.AcmeAccount.Key.RawData);

            _acmeContext = new AcmeContext(acmeCert.AcmeAccount.IsAcmeStaging
                ? WellKnownServers.LetsEncryptStagingV2
                : WellKnownServers.LetsEncryptV2, accountKey);
        }

        public async Task<AcmeOrder> BeginOrder()
        {
            var domains = new List<string> { _acmeCertificate.Subject };

            if (!string.IsNullOrWhiteSpace(_acmeCertificate.SANs))
            {
                var sans = _acmeCertificate.SANs
                    .Split(new[] { "\r\n", "\r", "\n", ",", ";" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .ToList();

                domains.AddRange(sans);
            }

            _acmeOrder = new AcmeOrder
            {
                AcmeCertificate = _acmeCertificate,
                DateCreated = DateTime.UtcNow,
                Status = AcmeOrderStatus.Created
            };

            _acmeCertificate.AcmeOrders.Add(_acmeOrder);

            try
            {
                _order = await _acmeContext.NewOrder(domains);
                _logger.LogDebug($"Order created: {_order.Location}");

                // Get authorizations for the new order which we'll then place
                var authz = await _order.Authorizations();

                // Track all auth requests to the corresponding validation and 
                // subsequent completion and certificate response
                _authChallengeContainers = new List<AuthChallengeContainer>();

                foreach (var auth in authz)
                {
                    var resource = await auth.Resource();
                    var domain = resource.Identifier.Value;

                    _authChallengeContainers.Add(new AuthChallengeContainer
                    {
                        AuthorizationContext = auth,
                        Domain = domain,
                        ChallengeContextTask = _acmeCertificate.IsDnsChallengeType() ? auth.Dns() : auth.Http(),
                    });
                }

                await Task.WhenAll(_authChallengeContainers.Select(x => x.ChallengeContextTask).ToList());

                _acmeOrder.Status = AcmeOrderStatus.Challenging;

                var newRequests = new List<AcmeRequest>();

                foreach (var cc in _authChallengeContainers)
                {
                    cc.ChallengeContext = cc.ChallengeContextTask.Result;

                    var acmeReq = new AcmeRequest
                    {
                        KeyAuthorization = cc.ChallengeContext.KeyAuthz,
                        Token = cc.ChallengeContext.Token,
                        DateCreated = DateTime.UtcNow,
                        Domain = cc.Domain,
                        DnsTxtValue = _acmeContext.AccountKey.DnsTxt(cc.ChallengeContext.Token),
                        AcmeOrder = _acmeOrder
                    };
                    _acmeOrder.AcmeRequests.Add(acmeReq);
                    newRequests.Add(acmeReq);

                    cc.AcmeRequest = acmeReq;
                }
            }
            catch (AcmeRequestException e)
            {
                _logger.LogError(e, "Error requesting order");
                _acmeOrder.Status = AcmeOrderStatus.Error;
                _acmeOrder.Errors = e.Message;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error creating order");
                _acmeOrder.Status = AcmeOrderStatus.Error;
            }

            return _acmeOrder;
        }

        public bool SetDnsRecords(DnsSettingsContainer dnsSettings)
        {
            if (string.IsNullOrWhiteSpace(dnsSettings.DnsSetupScript))
            {
                _logger.LogDebug("No DNS set script specified");
                return false;
            }
            var exitCodes = new List<int>();

            foreach (var req in _acmeOrder.AcmeRequests)
            {
                var registrableDomain = DomainParser.RegistrableDomain(req.Domain);
                var subdomain = "_acme-challenge";
                if (!string.IsNullOrWhiteSpace(subdomain))
                {
                    subdomain = subdomain + "." + DomainParser.Subdomain(req.Domain);
                }

                var transformedArgs = dnsSettings.DnsSetupScriptArguments?
                   .Replace("{{Domain}}", registrableDomain)
                   .Replace("{{Record}}", subdomain)
                   .Replace("{{Value}}", req.DnsTxtValue);

                var exitCode = RunProcess(dnsSettings.DnsSetupScript,
                    transformedArgs,
                    dnsSettings.TransformEnvironmentVariables());
                exitCodes.Add(exitCode);
            }

            return exitCodes.Any(x => x != 0);
        }

        public async Task<bool> ValidateDnsRecords()
        {
            foreach (var req in _acmeOrder.AcmeRequests)
            {
                await _dnsVerifier.PreValidate(req.Domain, req.Token);
            }
            return true;
        }

        public bool CleanupDnsRecords(DnsSettingsContainer dnsSettings)
        {
            if (string.IsNullOrWhiteSpace(dnsSettings.DnsCleanupScript))
            {
                _logger.LogDebug("No DNS cleanup script specified");
                return false;
            }
            var exitCodes = new List<int>();

            foreach (var req in _acmeOrder.AcmeRequests)
            {
                var registrableDomain = DomainParser.RegistrableDomain(req.Domain);
                var subdomain = "_acme-challenge";
                if (!string.IsNullOrWhiteSpace(subdomain))
                {
                    subdomain = subdomain + "." + DomainParser.Subdomain(req.Domain);
                }

                var transformedArgs = dnsSettings.DnsCleanupScriptArguments?
                   .Replace("{{Domain}}", registrableDomain)
                   .Replace("{{Record}}", subdomain)
                   .Replace("{{Value}}", req.DnsTxtValue);

                var exitCode = RunProcess(dnsSettings.DnsCleanupScript,
                    transformedArgs,
                    dnsSettings.TransformEnvironmentVariables());
                exitCodes.Add(exitCode);
            }

            return exitCodes.Any(x => x != 0);
        }

        private int RunProcess(string file, string args, List<KeyValuePair<string, string>> envVars)
        {
            try
            {
                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };

                foreach (var kv in envVars)
                {
                    process.StartInfo.EnvironmentVariables[kv.Key] = kv.Value;
                }

                using (process)
                {
                    process.Start();
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();

                    process.OutputDataReceived += (sender, outputLine) => { if (outputLine.Data != null) stdout.AppendLine(outputLine.Data); };
                    process.ErrorDataReceived += (sender, errorLine) => { if (errorLine.Data != null) stderr.AppendLine(errorLine.Data); };
                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();

                    var exited = process.WaitForExit(PROCESS_WAIT_MS);
                    if (!exited)
                    {
                        process.Kill(true);
                    }

                    _logger.LogDebug($"Process completed with exit code {process.ExitCode}");
                    _logger.LogDebug($"Process stdout:{Environment.NewLine}{stdout}");
                    _logger.LogDebug($"Process stderr:{Environment.NewLine}{stderr}");

                    return process.ExitCode;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error running process");
            }
            return -1;
        }

        public async Task<AcmeOrder> Validate()
        {
            if (_acmeOrder.Status != AcmeOrderStatus.Challenging)
            {
                return _acmeOrder;
            }
            try
            {
                foreach (var cc in _authChallengeContainers)
                {
                    cc.ChallengeTask = cc.ChallengeContext.Validate();
                }

                await Task.WhenAll(_authChallengeContainers.Select(x => x.ChallengeTask).ToList());

                foreach (var cc in _authChallengeContainers)
                {
                    cc.Challenge = cc.ChallengeTask.Result;
                }

                _acmeOrder.Status = AcmeOrderStatus.Validating;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error validating order");
                _acmeOrder.Status = AcmeOrderStatus.Error;
            }
            return _acmeOrder;
        }

        public async Task<AcmeOrder> Complete()
        {
            if (_acmeOrder.Status != AcmeOrderStatus.Validating)
            {
                return _acmeOrder;
            }

            foreach (var cc in _authChallengeContainers)
            {
                cc.AuthorizationTask = cc.AuthorizationContext.Resource();
            }

            var attempts = 5;
            do
            {
                // Kick off the authorization tasks for the tasks that haven't been run yet
                await Task.WhenAll(_authChallengeContainers
                    .Where(x => !x.AuthorizationTask.IsCompleted)
                    .Select(x => x.AuthorizationTask)
                    .ToList());

                var incompletes = 0;
                // After running the tasks, find all incomplete authz
                foreach (var cc in _authChallengeContainers)
                {
                    var status = cc.AuthorizationTask.Result.Status;
                    var completed = status == AuthorizationStatus.Valid || 
                                    status == AuthorizationStatus.Invalid;
                    if (!completed)
                    {
                        incompletes++;

                        // Update the task such that it's a new task and it will be awaited above
                        cc.AuthorizationTask = cc.AuthorizationContext.Resource();
                    }
                    else
                    {
                        cc.Authorization = cc.AuthorizationTask.Result;
                    }
                }

                // Find incomplete ones and try again
                _logger.LogDebug($"{incompletes} incomplete authorizations.");

                if (incompletes == 0)
                {
                    break;
                }

                await Task.Delay(5000);

            } while (attempts-- > 0);

            // All authorizations have completed, save the results
            foreach (var cc in _authChallengeContainers)
            {
                cc.Authorization = cc.AuthorizationTask.Result;
            }
            
            // At this point, they're all complete and need to see which are valid/invalid
            // and obtain the cert if possible.
            try
            {
                var invalidResp = _authChallengeContainers
                    .SelectMany(x => x.Authorization.Challenges)
                    .Where(x => x.Error != null)
                    .ToList();

                var errors = string.Join("\r\n", invalidResp
                    .Select(x => $"{x.Error.Status} {x.Error.Type} {x.Error.Detail}"));

                _acmeOrder.RequestCount = _authChallengeContainers.Count;
                _acmeOrder.InvalidResponseCount = invalidResp.Count;
                _acmeOrder.Errors = errors;
                _acmeOrder.Status = AcmeOrderStatus.Completed;

                if (invalidResp.Count > 0)
                {
                    _acmeOrder.Status = AcmeOrderStatus.Invalid;
                    return _acmeOrder;
                }

                var cert = await _order.Generate(
                    new CsrInfo
                    {
                        CommonName = _acmeCertificate.CsrCommonName,
                        CountryName = _acmeCertificate.CsrCountryName,
                        Locality = _acmeCertificate.CsrLocality,
                        Organization = _acmeCertificate.CsrOrganization,
                        OrganizationUnit = _acmeCertificate.CsrOrganizationUnit,
                        State = _acmeCertificate.CsrState
                    }, KeyFactory.FromPem(_acmeCertificate.Key.RawData));

                var certBytes = cert.Certificate.ToDer();

                var xCert = new X509Certificate2(certBytes);
                var domainCert = DomainCertificate.FromX509Certificate2(xCert, CertificateSource.AcmeCertificate);
                xCert.Dispose();

                _acmeOrder.RawDataPem = cert.ToPem();
                _acmeOrder.DomainCertificate = domainCert;

                return _acmeOrder;
            }
            catch (AcmeRequestException e)
            {
                _acmeOrder.Status = AcmeOrderStatus.Error;
                _acmeOrder.Errors = $"{e.Error.Status} {e.Error.Type} {e.Error.Detail}";
            }
            catch (Exception)
            {
                _acmeOrder.Status = AcmeOrderStatus.Error;
                _acmeOrder.Errors = "Unknown Error";
            }
            return _acmeOrder;
        }
    }

    public class AuthChallengeContainer
    {
        public AcmeRequest AcmeRequest { get; set; }
        public IAuthorizationContext AuthorizationContext { get; set; }
        public Task<IChallengeContext> ChallengeContextTask { get; set; }
        public IChallengeContext ChallengeContext { get; set; }
        public Task<Challenge> ChallengeTask { get; set; }
        public Challenge Challenge { get; set; }
        public Task<Authorization> AuthorizationTask { get; set; }
        public Authorization Authorization { get; set; }
        public string Domain { get; set; }
    }
}
