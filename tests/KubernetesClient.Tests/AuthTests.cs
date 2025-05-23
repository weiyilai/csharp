using k8s.Authentication;
using k8s.Autorest;
using k8s.Exceptions;
using k8s.KubeConfigModels;
using k8s.Models;
using k8s.Tests.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace k8s.Tests
{
    public class AuthTests
    {
        private readonly ITestOutputHelper testOutput;

        public AuthTests(ITestOutputHelper testOutput)
        {
            this.testOutput = testOutput;
        }

        private static HttpOperationResponse<V1PodList> ExecuteListPods(IKubernetes client)
        {
            return client.CoreV1.ListNamespacedPodWithHttpMessagesAsync("default").Result;
        }

        [Fact]
        public void Anonymous()
        {
            using (var server = new MockKubeApiServer(testOutput))
            {
                var client = new Kubernetes(new KubernetesClientConfiguration { Host = server.Uri.ToString() });

                var listTask = ExecuteListPods(client);

                Assert.True(listTask.Response.IsSuccessStatusCode);
                Assert.Single(listTask.Body.Items);
            }

            using (var server = new MockKubeApiServer(testOutput, cxt =>
            {
                cxt.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return Task.FromResult(false);
            }))
            {
                var client = new Kubernetes(new KubernetesClientConfiguration { Host = server.Uri.ToString() });
                ShouldThrowUnauthorized(client);
            }
        }

        private static void PeelAggregate(Action testcode)
        {
            try
            {
                testcode();
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions.Count == 1)
                {
                    throw e.InnerExceptions.First();
                }

                throw;
            }
        }

        [Fact]
        public void BasicAuth()
        {
            const string testName = "test_name";
            const string testPassword = "test_password";

            using (var server = new MockKubeApiServer(testOutput, cxt =>
            {
                var header = cxt.Request.Headers["Authorization"].FirstOrDefault();

                var expect = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{testName}:{testPassword}")))
                    .ToString();

                if (header != expect)
                {
                    cxt.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            }))
            {
                {
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        Username = testName,
                        Password = testPassword,
                    });

                    var listTask = ExecuteListPods(client);
                    Assert.True(listTask.Response.IsSuccessStatusCode);
                    Assert.Single(listTask.Body.Items);
                }

                {
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        Username = "wrong name",
                        Password = testPassword,
                    });

                    ShouldThrowUnauthorized(client);
                }

                {
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        Username = testName,
                        Password = "wrong password",
                    });

                    ShouldThrowUnauthorized(client);
                }

                {
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        Username = "both wrong",
                        Password = "wrong password",
                    });

                    ShouldThrowUnauthorized(client);
                }

                {
                    var client = new Kubernetes(new KubernetesClientConfiguration { Host = server.Uri.ToString() });
                    ShouldThrowUnauthorized(client);
                }

                {
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        Username = "xx",
                    });

                    ShouldThrowUnauthorized(client);
                }
            }
        }

        // this test doesn't work on OSX
        [OperatingSystemDependentFact(Exclude = OperatingSystems.OSX)]
        public void Cert()
        {
            var serverCertificateData = File.ReadAllText("assets/apiserver-pfx-data.txt");

            var clientCertificateKeyData = File.ReadAllText("assets/client-key-data.txt");
            var clientCertificateData = File.ReadAllText("assets/client-certificate-data.txt");

            X509Certificate2 serverCertificate = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                using (var serverCertificateStream =
                    new MemoryStream(Convert.FromBase64String(serverCertificateData)))
                {
                    serverCertificate = OpenCertificateStore(serverCertificateStream);
                }
            }
            else
            {
                serverCertificate = new X509Certificate2(Convert.FromBase64String(serverCertificateData), "");
            }

            var clientCertificate = new X509Certificate2(Convert.FromBase64String(clientCertificateData), "");

            var clientCertificateValidationCalled = false;

            using (var server = new MockKubeApiServer(testOutput, listenConfigure: options =>
            {
                options.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    ClientCertificateValidation = (certificate, chain, valid) =>
                    {
                        clientCertificateValidationCalled = true;
                        return clientCertificate.Equals(certificate);
                    },
                });
            }))
            {
                {
                    clientCertificateValidationCalled = false;
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        ClientCertificateData = clientCertificateData,
                        ClientCertificateKeyData = clientCertificateKeyData,
                        SslCaCerts = new X509Certificate2Collection(serverCertificate),
                        SkipTlsVerify = false,
                    });

                    var listTask = ExecuteListPods(client);

                    Assert.True(clientCertificateValidationCalled);
                    Assert.True(listTask.Response.IsSuccessStatusCode);
                    Assert.Single(listTask.Body.Items);
                }

                {
                    clientCertificateValidationCalled = false;
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        ClientCertificateData = clientCertificateData,
                        ClientCertificateKeyData = clientCertificateKeyData,
                        SkipTlsVerify = true,
                    });

                    var listTask = ExecuteListPods(client);

                    Assert.True(clientCertificateValidationCalled);
                    Assert.True(listTask.Response.IsSuccessStatusCode);
                    Assert.Single(listTask.Body.Items);
                }

                {
                    clientCertificateValidationCalled = false;
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        ClientCertificateFilePath =
                            "assets/client.crt", // TODO amazoning why client.crt != client-data.txt
                        ClientKeyFilePath = "assets/client.key",
                        SkipTlsVerify = true,
                    });

                    Assert.ThrowsAny<Exception>(() => ExecuteListPods(client));
                    Assert.True(clientCertificateValidationCalled);
                }

                {
                    clientCertificateValidationCalled = false;
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        SkipTlsVerify = true,
                    });

                    Assert.ThrowsAny<Exception>(() => ExecuteListPods(client));
                    Assert.False(clientCertificateValidationCalled);
                }
            }
        }

        [OperatingSystemDependentFact(Exclude = OperatingSystems.OSX)]
        public void ExternalCertificate()
        {
            const string name = "testing_irrelevant";

            var serverCertificateData = Convert.FromBase64String(File.ReadAllText("assets/apiserver-pfx-data.txt"));

            var clientCertificateKeyData = Convert.FromBase64String(File.ReadAllText("assets/client-key-data.txt"));
            var clientCertificateData = Convert.FromBase64String(File.ReadAllText("assets/client-certificate-data.txt"));

            X509Certificate2 serverCertificate = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                using (var serverCertificateStream = new MemoryStream(serverCertificateData))
                {
                    serverCertificate = OpenCertificateStore(serverCertificateStream);
                }
            }
            else
            {
                serverCertificate = new X509Certificate2(serverCertificateData, "");
            }

            var clientCertificate = new X509Certificate2(clientCertificateData, "");

            var clientCertificateValidationCalled = false;

            using (var server = new MockKubeApiServer(testOutput, listenConfigure: options =>
            {
                options.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    ClientCertificateValidation = (certificate, chain, valid) =>
                    {
                        clientCertificateValidationCalled = true;
                        return clientCertificate.Equals(certificate);
                    },
                });
            }))
            {
                {
                    var clientCertificateText = Encoding.ASCII.GetString(clientCertificateData).Replace("\n", "\\n");
                    var clientCertificateKeyText = Encoding.ASCII.GetString(clientCertificateKeyData).Replace("\n", "\\n");
                    var responseJson = $"{{\"apiVersion\":\"testingversion\",\"status\":{{\"clientCertificateData\":\"{clientCertificateText}\",\"clientKeyData\":\"{clientCertificateKeyText}\"}}}}";
                    var kubernetesConfig = GetK8SConfiguration(server.Uri.ToString(), responseJson, name);
                    var clientConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(kubernetesConfig, name);
                    var client = new Kubernetes(clientConfig);
                    var listTask = ExecuteListPods(client);
                    Assert.True(listTask.Response.IsSuccessStatusCode);
                    Assert.Single(listTask.Body.Items);
                }

                {
                    var clientCertificateText = File.ReadAllText("assets/client.crt").Replace("\n", "\\n");
                    var clientCertificateKeyText = File.ReadAllText("assets/client.key").Replace("\n", "\\n");
                    var responseJson = $"{{\"apiVersion\":\"testingversion\",\"status\":{{\"clientCertificateData\":\"{clientCertificateText}\",\"clientKeyData\":\"{clientCertificateKeyText}\"}}}}";
                    var kubernetesConfig = GetK8SConfiguration(server.Uri.ToString(), responseJson, name);
                    var clientConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(kubernetesConfig, name);
                    var client = new Kubernetes(clientConfig);
                    Assert.ThrowsAny<Exception>(() => ExecuteListPods(client));
                    Assert.True(clientCertificateValidationCalled);
                }
            }
        }

        [Fact]
        public void ExternalToken()
        {
            const string token = "testingtoken";
            const string name = "testing_irrelevant";

            using (var server = new MockKubeApiServer(testOutput, cxt =>
             {
                 var header = cxt.Request.Headers["Authorization"].FirstOrDefault();

                 var expect = new AuthenticationHeaderValue("Bearer", token).ToString();

                 if (header != expect)
                 {
                     cxt.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                     return Task.FromResult(false);
                 }

                 return Task.FromResult(true);
             }))
            {
                {
                    var responseJson = $"{{\"apiVersion\":\"testingversion\",\"status\":{{\"token\":\"{token}\"}}}}";
                    var kubernetesConfig = GetK8SConfiguration(server.Uri.ToString(), responseJson, name);
                    var clientConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(kubernetesConfig, name);
                    var client = new Kubernetes(clientConfig);
                    var listTask = ExecuteListPods(client);
                    Assert.True(listTask.Response.IsSuccessStatusCode);
                    Assert.Single(listTask.Body.Items);
                }

                {
                    var responseJson = "{\"apiVersion\":\"testingversion\",\"status\":{\"token\":\"wrong_token\"}}";
                    var kubernetesConfig = GetK8SConfiguration(server.Uri.ToString(), responseJson, name);
                    var clientConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(kubernetesConfig, name);
                    var client = new Kubernetes(clientConfig);
                    ShouldThrowUnauthorized(client);
                }
            }
        }

        [Fact]
        public void Token()
        {
            const string token = "testingtoken";

            using (var server = new MockKubeApiServer(testOutput, cxt =>
            {
                var header = cxt.Request.Headers["Authorization"].FirstOrDefault();

                var expect = new AuthenticationHeaderValue("Bearer", token).ToString();

                if (header != expect)
                {
                    cxt.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            }))
            {
                {
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        AccessToken = token,
                    });

                    var listTask = ExecuteListPods(client);
                    Assert.True(listTask.Response.IsSuccessStatusCode);
                    Assert.Single(listTask.Body.Items);
                }

                {
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        AccessToken = "wrong token",
                    });

                    ShouldThrowUnauthorized(client);
                }

                {
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        Username = "wrong name",
                        Password = "same password",
                    });

                    ShouldThrowUnauthorized(client);
                }

                {
                    var client = new Kubernetes(new KubernetesClientConfiguration { Host = server.Uri.ToString() });

                    ShouldThrowUnauthorized(client);
                }
            }
        }

        [Fact]
        public void Oidc()
        {
            var clientId = "CLIENT_ID";
            var clientSecret = "CLIENT_SECRET";
            var idpIssuerUrl = "https://idp.issuer.url";
            var unexpiredIdToken = "eyJhbGciOiJIUzI1NiJ9.eyJpYXQiOjAsImV4cCI6MjAwMDAwMDAwMH0.8Ata5uKlrqYfeIaMwS91xVgVFHu7ntHx1sGN95i2Zho";
            var expiredIdToken = "eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjB9.f37LFpIw_XIS5TZt3wdtEjjyCNshYy03lOWpyDViRM0";
            var refreshToken = "REFRESH_TOKEN";

            using (var server = new MockKubeApiServer(testOutput, cxt =>
            {
                var header = cxt.Request.Headers["Authorization"].FirstOrDefault();

                var expect = new AuthenticationHeaderValue("Bearer", unexpiredIdToken).ToString();

                if (header != expect)
                {
                    cxt.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            }))
            {
                {
                    // use unexpired id token as bearer, do not attempt to refresh
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        AccessToken = unexpiredIdToken,
                        TokenProvider = new OidcTokenProvider(clientId, clientSecret, idpIssuerUrl, unexpiredIdToken, refreshToken),
                    });

                    var listTask = ExecuteListPods(client);
                    Assert.True(listTask.Response.IsSuccessStatusCode);
                    Assert.Single(listTask.Body.Items);
                }

                {
                    // attempt to refresh id token when expired
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        AccessToken = expiredIdToken,
                        TokenProvider = new OidcTokenProvider(clientId, clientSecret, idpIssuerUrl, expiredIdToken, refreshToken),
                    });

                    try
                    {
                        PeelAggregate(() => ExecuteListPods(client));
                        Assert.Fail("should not be here");
                    }
                    catch (KubernetesClientException e)
                    {
                        Assert.StartsWith("Unable to refresh OIDC token.", e.Message);
                    }
                }

                {
                    // attempt to refresh id token when null
                    var client = new Kubernetes(new KubernetesClientConfiguration
                    {
                        Host = server.Uri.ToString(),
                        AccessToken = expiredIdToken,
                        TokenProvider = new OidcTokenProvider(clientId, clientSecret, idpIssuerUrl, null, refreshToken),
                    });

                    try
                    {
                        PeelAggregate(() => ExecuteListPods(client));
                        Assert.Fail("should not be here");
                    }
                    catch (KubernetesClientException e)
                    {
                        Assert.StartsWith("Unable to refresh OIDC token.", e.Message);
                    }
                }
            }
        }

        private static void ShouldThrowUnauthorized(Kubernetes client)
        {
            try
            {
                PeelAggregate(() => ExecuteListPods(client));
                Assert.Fail("should not be here");
            }
            catch (HttpOperationException e)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, e.Response.StatusCode);
            }
        }

        private X509Certificate2 OpenCertificateStore(Stream stream)
        {
            var store = new Pkcs12Store();
            store.Load(stream, new char[] { });

            var keyAlias = store.Aliases.Cast<string>().SingleOrDefault(store.IsKeyEntry);

            var key = (RsaPrivateCrtKeyParameters)store.GetKey(keyAlias).Key;
            var bouncyCertificate = store.GetCertificate(keyAlias).Certificate;

            var certificate = new X509Certificate2(DotNetUtilities.ToX509Certificate(bouncyCertificate));
            var parameters = DotNetUtilities.ToRSAParameters(key);

            var rsa = new RSACryptoServiceProvider();
            rsa.ImportParameters(parameters);

            certificate = RSACertificateExtensions.CopyWithPrivateKey(certificate, rsa);

            return certificate;
        }

        private K8SConfiguration GetK8SConfiguration(string serverUri, string responseJson, string name)
        {
            const string username = "testinguser";

            var contexts = new List<Context>
            {
                new Context { Name = name, ContextDetails = new ContextDetails { Cluster = name, User = username } },
            };
            {
                var clusters = new List<Cluster>
                {
                    new Cluster
                    {
                        Name = name,
                        ClusterEndpoint = new ClusterEndpoint { SkipTlsVerify = true, Server = serverUri },
                    },
                };

                var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "echo";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    command = "printf";
                }

                var arguments = new string[] { };
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    arguments = new[] { "/c", "echo", responseJson };
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    arguments = new[] { responseJson.Replace("\"", "\\\"") };
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    arguments = new[] { "\"%s\"", responseJson.Replace("\"", "\\\"") };
                }

                var users = new List<User>
                {
                    new User
                    {
                        Name = username,
                        UserCredentials = new UserCredentials
                        {
                            ExternalExecution = new ExternalExecution
                            {
                                ApiVersion = "testingversion",
                                Command = command,
                                Arguments = arguments.ToList(),
                            },
                        },
                    },
                };
                var kubernetesConfig = new K8SConfiguration { Clusters = clusters, Users = users, Contexts = contexts };
                return kubernetesConfig;
            }
        }
    }
}
