using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using k8s.Models;
using KubeOps.KubernetesClient;

namespace AzDORunner.Services
{
    public class WebhookCertificateManager
    {
        private readonly IKubernetesClient _kubernetesClient;
        private readonly ILogger<WebhookCertificateManager> _logger;
        private readonly string _namespace;
        private readonly string _serviceName;
        private readonly string _secretName;
        private readonly string _validatingWebhookName;
        private readonly string _mutatingWebhookName;
        private readonly int _certValidityDays = 365;
        private readonly int _certRenewBeforeDays = 30;

        public WebhookCertificateManager(
            IKubernetesClient kubernetesClient,
            ILogger<WebhookCertificateManager> logger)
        {
            _kubernetesClient = kubernetesClient;
            _logger = logger;
            _namespace = Environment.GetEnvironmentVariable("POD_NAMESPACE") ?? throw new InvalidOperationException("POD_NAMESPACE env var is required");
            _serviceName = Environment.GetEnvironmentVariable("SERVICE_NAME") ?? throw new InvalidOperationException("SERVICE_NAME env var is required");
            _secretName = $"{_serviceName}-ca";
            _validatingWebhookName = "azdo-runner-validating-webhook";
            _mutatingWebhookName = "azdo-runner-mutating-webhook";
        }

        public void Reconcile()
        {
            var secret = GetOrCreateSecret();
            var caBundle = secret.Data["ca.crt"];
            ReconcileWebhookConfigurations(caBundle);
        }

        private V1Secret GetOrCreateSecret()
        {
            var secret = TryGetSecret();
            if (secret != null && !ShouldRotate(secret))
            {
                _logger.LogInformation("Webhook TLS secret is valid, no rotation needed.");
                return secret;
            }

            _logger.LogInformation("Generating new self-signed certificate for webhook.");
            var (cert, key, ca) = GenerateSelfSignedCert();
            var newSecret = new V1Secret(
                metadata: new V1ObjectMeta(name: _secretName, namespaceProperty: _namespace),
                data: new Dictionary<string, byte[]>
                {
                    ["tls.crt"] = cert,
                    ["tls.key"] = key,
                    ["ca.crt"] = ca
                },
                type: "kubernetes.io/tls"
            );
            UpsertSecret(newSecret);
            return newSecret;
        }

        private V1Secret? TryGetSecret()
        {
            try
            {
                return _kubernetesClient.Get<V1Secret>(_secretName, _namespace);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Secret {SecretName} not found", _secretName);
                return null;
            }
        }

        private bool ShouldRotate(V1Secret secret)
        {
            if (!secret.Data.TryGetValue("tls.crt", out var certBytes))
                return true;
            try
            {
                // Use Import with X509ContentType.Cert, suppress obsolete warning
#pragma warning disable SYSLIB0026
                using var cert = new X509Certificate2();
                cert.Import(certBytes, (string?)null, X509KeyStorageFlags.DefaultKeySet);
#pragma warning restore SYSLIB0026
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

        private (byte[] cert, byte[] key, byte[] ca) GenerateSelfSignedCert()
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
            var certBytes = cert.Export(X509ContentType.Cert);
            var keyBytes = rsa.ExportPkcs8PrivateKey();
            return (cert.Export(X509ContentType.Pfx), keyBytes, certBytes);
        }

        private List<string> GetServiceDnsNames()
        {
            var names = new List<string>
            {
                _serviceName,
                $"{_serviceName}.{_namespace}",
                $"{_serviceName}.{_namespace}.svc",
                $"{_serviceName}.{_namespace}.svc.cluster.local"
            };
            return names;
        }

        private void UpsertSecret(V1Secret secret)
        {
            try
            {
                var existing = _kubernetesClient.Get<V1Secret>(_secretName, _namespace);
                if (existing != null)
                {
                    secret.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    _kubernetesClient.Update(secret);
                }
                else
                {
                    _kubernetesClient.Create(secret);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert webhook TLS secret");
                throw;
            }
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
                        Name = "v1runnerpool.kb.io",
                        ClientConfig = new Admissionregistrationv1WebhookClientConfig
                        {
                            CaBundle = caBundle,
                            Service = new Admissionregistrationv1ServiceReference
                            {
                                Name = _serviceName,
                                NamespaceProperty = _namespace,
                                Path = "/validate-v1-runnerpool"
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
                        SideEffects = "None"
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
                }
                else
                {
                    _kubernetesClient.Create(webhook);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert ValidatingWebhookConfiguration");
                throw;
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
                        Name = "v1runnerpool.kb.io",
                        ClientConfig = new Admissionregistrationv1WebhookClientConfig
                        {
                            CaBundle = caBundle,
                            Service = new Admissionregistrationv1ServiceReference
                            {
                                Name = _serviceName,
                                NamespaceProperty = _namespace,
                                Path = "/mutate-v1-runnerpool"
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
                        SideEffects = "None"
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
                }
                else
                {
                    _kubernetesClient.Create(webhook);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert MutatingWebhookConfiguration");
                throw;
            }
        }
    }
}
