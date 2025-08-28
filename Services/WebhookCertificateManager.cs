using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using k8s.Models;
using KubeOps.KubernetesClient;


namespace AzDORunner.Services
{
    public class WebhookCertificateManager
    {
        private readonly ILogger<WebhookCertificateManager> _logger;
        private readonly IKubernetesClient _kubernetesClient;
        private readonly string _namespace;
        private readonly string _serviceName;
        private readonly string _validatingWebhookName;
        private readonly string _mutatingWebhookName;
        private readonly int _certValidityDays = 365;
        private readonly int _certRenewBeforeDays = 30;

        public WebhookCertificateManager(
            ILogger<WebhookCertificateManager> logger,
            IKubernetesClient kubernetesClient)
        {
            _logger = logger;
            _kubernetesClient = kubernetesClient;
            _namespace = Environment.GetEnvironmentVariable("POD_NAMESPACE") ?? throw new InvalidOperationException("POD_NAMESPACE env var is required");
            _serviceName = Environment.GetEnvironmentVariable("SERVICE_NAME") ?? throw new InvalidOperationException("SERVICE_NAME env var is required");
            _validatingWebhookName = "azdo-runner-validating-webhook";
            _mutatingWebhookName = "azdo-runner-mutating-webhook";
        }

        public void Reconcile()
        {
            var (cert, key, ca) = GetOrCreateOrUpdateCertFiles();
            ReconcileWebhookConfigurations(ca);
        }

        private (byte[] cert, byte[] key, byte[] ca) GetOrCreateOrUpdateCertFiles()
        {
            var certDir = Environment.GetEnvironmentVariable("CERT_DIR") ?? "/certs";
            var certPath = Path.Combine(certDir, "tls.crt");
            var keyPath = Path.Combine(certDir, "tls.key");
            var caPath = Path.Combine(certDir, "ca.crt");

            bool needsNewCert = false;
            byte[] certBytes = Array.Empty<byte>();
            byte[] keyBytes = Array.Empty<byte>();
            byte[] caBytes = Array.Empty<byte>();
            string certPem = string.Empty;
            string keyPem = string.Empty;
            string caPem = string.Empty;

            try
            {
                if (File.Exists(certPath) && File.Exists(keyPath) && File.Exists(caPath))
                {
                    certPem = File.ReadAllText(certPath);
                    keyPem = File.ReadAllText(keyPath);
                    caPem = File.ReadAllText(caPath);
                    certBytes = System.Text.Encoding.UTF8.GetBytes(certPem);
                    keyBytes = System.Text.Encoding.UTF8.GetBytes(keyPem);
                    caBytes = System.Text.Encoding.UTF8.GetBytes(caPem);
                    var certDer = ExtractDerFromPem(certPem, "CERTIFICATE");
                    if (ShouldRotate(certDer))
                    {
                        needsNewCert = true;
                    }
                }
                else
                {
                    needsNewCert = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read cert files, will generate new ones.");
                needsNewCert = true;
            }

            if (needsNewCert)
            {
                _logger.LogInformation("Generating new self-signed certificate for webhook (file-based).");
                (certPem, keyPem, caPem) = GenerateSelfSignedCertPem();
                certBytes = System.Text.Encoding.UTF8.GetBytes(certPem);
                keyBytes = System.Text.Encoding.UTF8.GetBytes(keyPem);
                caBytes = System.Text.Encoding.UTF8.GetBytes(caPem);
                try
                {
                    Directory.CreateDirectory(certDir);
                    File.WriteAllText(certPath, certPem);
                    File.WriteAllText(keyPath, keyPem);
                    File.WriteAllText(caPath, caPem);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write cert files to emptyDir volume.");
                    throw;
                }
            }
            return (certBytes, keyBytes, caBytes);
        }


        private (string certPem, string keyPem, string caPem) GenerateSelfSignedCertPem()
        {
            var sanDnsNames = GetServiceDnsNames();
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                $"CN={_serviceName}.{_namespace}.svc",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var dns in sanDnsNames)
                sanBuilder.AddDnsName(dns);
            req.CertificateExtensions.Add(sanBuilder.Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(_certValidityDays));
            // Export certificate and key as PEM
            var certPem = new string(PemEncoding.Write("CERTIFICATE", cert.RawData));
            var keyBytes = rsa.ExportPkcs8PrivateKey();
            var keyPem = new string(PemEncoding.Write("PRIVATE KEY", keyBytes));
            // For CA, use the same cert (self-signed)
            var caPem = certPem;
            return (certPem, keyPem, caPem);
        }

        private bool ShouldRotate(byte[] certDer)
        {
            try
            {
#pragma warning disable SYSLIB0057
                using var cert = new X509Certificate2(certDer);
#pragma warning restore SYSLIB0057
                var notAfter = cert.NotAfter;
                var now = DateTime.UtcNow;
                return (notAfter - now).TotalDays < _certRenewBeforeDays;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse certificate for rotation check.");
                return true;
            }
        }

        // Helper to extract DER bytes from PEM
        private static byte[] ExtractDerFromPem(string pem, string section)
        {
            var header = $"-----BEGIN {section}-----";
            var footer = $"-----END {section}-----";
            var start = pem.IndexOf(header) + header.Length;
            var end = pem.IndexOf(footer, start);
            var base64 = pem.Substring(start, end - start).Replace("\r", "").Replace("\n", "").Trim();
            return Convert.FromBase64String(base64);
        }

        // Helper to get DNS names for SAN
        private List<string> GetServiceDnsNames()
        {
            return new List<string>
            {
                _serviceName,
                $"{_serviceName}.{_namespace}",
                $"{_serviceName}.{_namespace}.svc",
                $"{_serviceName}.{_namespace}.svc.cluster.local"
            };
        }

        private void ReconcileWebhookConfigurations(byte[] caBundle)
        {
            ReconcileValidatingWebhook(caBundle);
            ReconcileMutatingWebhook(caBundle);
        }

        private void ReconcileValidatingWebhook(byte[] caBundle)
        {
            var webhook = new V1ValidatingWebhookConfiguration
            {
                Metadata = new V1ObjectMeta
                {
                    Name = _validatingWebhookName
                },
                Webhooks = new List<V1ValidatingWebhook>
                {
                    new V1ValidatingWebhook
                    {
                        Name = "validate.runnerpool.devops.opentools.mf.v1",
                        ClientConfig = new Admissionregistrationv1WebhookClientConfig
                        {
                            CaBundle = caBundle,
                            Service = new Admissionregistrationv1ServiceReference
                            {
                                Name = _serviceName,
                                NamespaceProperty = _namespace,
                                Path = "/validate/v1azdorunnerentity"
                            }
                        },
                        Rules = new List<V1RuleWithOperations>
                        {
                            new V1RuleWithOperations
                            {
                                Operations = new List<string>{"CREATE", "UPDATE"},
                                ApiGroups = new List<string>{"devops.opentools.mf"},
                                ApiVersions = new List<string>{"v1"},
                                Resources = new List<string>{"runnerpools"}
                            }
                        },
                        AdmissionReviewVersions = new List<string>{"v1"},
                        SideEffects = "None",
                        MatchPolicy = "Exact"
                    }
                }
            };
            UpsertValidatingWebhook(webhook);
        }

        private void UpsertValidatingWebhook(V1ValidatingWebhookConfiguration webhook)
        {
            try
            {
                var existing = _kubernetesClient.Get<V1ValidatingWebhookConfiguration>(webhook.Metadata.Name);
                if (existing != null)
                {
                    webhook.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    _kubernetesClient.Update(webhook);
                    _logger.LogInformation($"Updated ValidatingWebhookConfiguration '{webhook.Metadata.Name}'.");
                }
                else
                {
                    _kubernetesClient.Create(webhook);
                    _logger.LogInformation($"Created ValidatingWebhookConfiguration '{webhook.Metadata.Name}'.");
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("404"))
                {
                    _kubernetesClient.Create(webhook);
                    _logger.LogInformation($"Created ValidatingWebhookConfiguration '{webhook.Metadata.Name}' (IDK yet).");
                }
                else
                {
                    _logger.LogError(ex, $"Failed to upsert ValidatingWebhookConfiguration '{webhook.Metadata.Name}'.");
                    throw;
                }
            }
        }

        private void ReconcileMutatingWebhook(byte[] caBundle)
        {
            var webhook = new V1MutatingWebhookConfiguration
            {
                Metadata = new V1ObjectMeta
                {
                    Name = _mutatingWebhookName
                },
                Webhooks = new List<V1MutatingWebhook>
                {
                    new V1MutatingWebhook
                    {
                        Name = "mutate.runnerpool.devops.opentools.mf.v1",
                        ClientConfig = new Admissionregistrationv1WebhookClientConfig
                        {
                            CaBundle = caBundle,
                            Service = new Admissionregistrationv1ServiceReference
                            {
                                Name = _serviceName,
                                NamespaceProperty = _namespace,
                                Path = "/mutate/v1azdorunnerentity"
                            }
                        },
                        Rules = new List<V1RuleWithOperations>
                        {
                            new V1RuleWithOperations
                            {
                                Operations = new List<string>{"CREATE", "UPDATE"},
                                ApiGroups = new List<string>{"devops.opentools.mf"},
                                ApiVersions = new List<string>{"v1"},
                                Resources = new List<string>{"runnerpools"}
                            }
                        },
                        AdmissionReviewVersions = new List<string>{"v1"},
                        SideEffects = "None",
                        MatchPolicy = "Exact"
                    }
                }
            };
            UpsertMutatingWebhook(webhook);
        }

        private void UpsertMutatingWebhook(V1MutatingWebhookConfiguration webhook)
        {
            try
            {
                var existing = _kubernetesClient.Get<V1MutatingWebhookConfiguration>(webhook.Metadata.Name);
                if (existing != null)
                {
                    webhook.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    _kubernetesClient.Update(webhook);
                    _logger.LogInformation($"Updated MutatingWebhookConfiguration '{webhook.Metadata.Name}'.");
                }
                else
                {
                    _kubernetesClient.Create(webhook);
                    _logger.LogInformation($"Created MutatingWebhookConfiguration '{webhook.Metadata.Name}'.");
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("404"))
                {
                    _kubernetesClient.Create(webhook);
                    _logger.LogInformation($"Created MutatingWebhookConfiguration '{webhook.Metadata.Name}' (IDK yet).");
                }
                else
                {
                    _logger.LogError(ex, $"Failed to upsert MutatingWebhookConfiguration '{webhook.Metadata.Name}'.");
                    throw;
                }
            }
        }
    }
}
