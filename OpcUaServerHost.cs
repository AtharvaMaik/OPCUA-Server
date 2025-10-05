using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates; // fluent certificate builder API
using System;
using System.Collections.Generic;
using System.IO;  
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using MtpSimulator.Models;

namespace MtpSimulator
{
    /// <summary>
    /// Starts an OPC UA server and registers nodes from OpcUaItem list.
    /// Uses OPC Foundation .NET Standard SDK.
    /// </summary>
    public class OpcUaServerHost : IDisposable
    {
        private ApplicationInstance _application;
        private SimpleServer _server; // StandardServer-derived wrapper
        private bool _started = false;

        public string EndpointUrl { get; private set; }

        public OpcUaServerHost() { }

        public async Task StartAsync(IEnumerable<OpcUaItem> items, int port = 4840)
        {
            if (_started) return;

            var machine = Environment.MachineName;
            var appName = "MTP Simulator";
            var appUri = $"urn:{machine}:MtpSimulator";     // keep this stable once generated
            var baseAddress = $"opc.tcp://localhost:{port}";

            _application = new ApplicationInstance
            {
                ApplicationName = appName,
                ApplicationType = ApplicationType.Server,
                ConfigSectionName = "MtpSimulatorServer"
            };

            // ---- Programmatic ApplicationConfiguration (no app.config) ----
            var config = new ApplicationConfiguration
            {
                ApplicationName = appName,
                ApplicationUri = appUri,
                ApplicationType = ApplicationType.Server,

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = "pki/own",
                        SubjectName = $"CN={appName}"
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "pki/issuer"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "pki/trusted"
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "pki/rejected"
                    },
                    AutoAcceptUntrustedCertificates = true // DEV ONLY
                },

                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },

                ServerConfiguration = new ServerConfiguration
                {
                    BaseAddresses = new StringCollection { baseAddress },

                    // Expose only a None-security endpoint for debugging
                    SecurityPolicies = new ServerSecurityPolicyCollection
                    {
                        new ServerSecurityPolicy
                        {
                            SecurityMode      = MessageSecurityMode.None,
                            SecurityPolicyUri = SecurityPolicies.None
                        }
                    },

                    // Allow Anonymous user token
                    UserTokenPolicies = new UserTokenPolicyCollection
                    {
                        new UserTokenPolicy(UserTokenType.Anonymous)
                    }
                }
            };

            // ---- Ensure PKI directory structure exists ----
            Directory.CreateDirectory("pki/own");
            Directory.CreateDirectory("pki/issuer");
            Directory.CreateDirectory("pki/trusted");
            Directory.CreateDirectory("pki/rejected");

            await config.ValidateAsync(ApplicationType.Server);

            // Accept untrusted during dev; wire validator
            config.CertificateValidator = new CertificateValidator();
            config.CertificateValidator.CertificateValidation += (s, e) => { e.Accept = true; };

            _application.ApplicationConfiguration = config;

            // --------- Create a self-signed application certificate ----------
            const int KeySizeBits = 2048;   // RSA-2048
            const int LifetimeMonths = 120; // 10 years
            var dnsNames = new List<string> { "localhost", machine };

            var builder = CertificateFactory
                .CreateCertificate(
                    applicationUri: config.ApplicationUri,
                    applicationName: config.ApplicationName,
                    subjectName: $"CN={config.ApplicationName}",
                    domainNames: dnsNames
                )
                .SetNotBefore(DateTime.UtcNow.AddDays(-1))
                .SetLifeTime(LifetimeMonths)
                .SetRSAKeySize(KeySizeBits);

            X509Certificate2 appCert = builder.CreateForRSA();
            config.SecurityConfiguration.ApplicationCertificate.Certificate = appCert;

            // --------- Start server ---------
            _server = new SimpleServer(config, items);
            await _server.StartAsync(); // SimpleServer binds itself via ApplicationInstance.Start(this)

            EndpointUrl = baseAddress;
            _started = true;
        }

        public async Task StopAsync()
        {
            if (!_started) return;

            // Best-effort: stop simulation first
            try { await StopSimulationAsync(); } catch { /* ignore */ }

            await _server.StopAsync();
            _started = false;
        }

        // ---- Simulation forwarding (UI calls these) ----

        public Task StartSimulationAsync(int intervalMs = 1000)
        {
            if (!_started) throw new InvalidOperationException("Server is not started.");
            return _server.StartSimulationAsync(intervalMs);
        }

        public Task StopSimulationAsync()
        {
            if (!_started) return Task.CompletedTask;
            return _server.StopSimulationAsync();
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch { /* swallow on dispose */ }
        }
    }
}

