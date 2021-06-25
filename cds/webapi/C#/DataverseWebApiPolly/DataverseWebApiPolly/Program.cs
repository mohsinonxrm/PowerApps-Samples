using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Client;
using Polly;
using Polly.Extensions.Http;
using Polly.Registry;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DataverseWebApiPolly
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                ThreadPool.SetMinThreads(100, 1000); //Increase min threads available on-demand
                ServicePointManager.DefaultConnectionLimit = 50000;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;

                await RunAsync();//.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(ex.Message);
                Console.ResetColor();
            }

            Console.WriteLine("Press any key to exit");
            Console.ReadKey();
        }

        private static async Task RunAsync()
        {
            AuthenticationConfig config = AuthenticationConfig.ReadFromJsonFile("appsettings.json");

            // You can run this sample using ClientSecret or Certificate. The code will differ only when instantiating the IConfidentialClientApplication
            bool isUsingClientSecret = AppUsesClientSecret(config);

            // Even if this is a console application here, a daemon application is a confidential client application
            IConfidentialClientApplication app;

            if (isUsingClientSecret)
            {
                app = ConfidentialClientApplicationBuilder.Create(config.ClientId)
                    .WithClientSecret(config.ClientSecret)
                    .WithAuthority(new Uri(config.Authority))
                    .Build();
            }

            else
            {
                X509Certificate2 certificate = ReadCertificate(config.CertificateName);
                app = ConfidentialClientApplicationBuilder.Create(config.ClientId)
                    .WithCertificate(certificate)
                    .WithAuthority(new Uri(config.Authority))
                    .Build();
            }

            // With client credentials flows the scopes is ALWAYS of the shape "resource/.default", as the 
            // application permissions need to be set statically (in the portal or by PowerShell), and then granted by
            // a tenant administrator
            string[] scopes = new string[] { "https://org.crm.dynamics.com/.default" };

            AuthenticationResult result = null;
            try
            {
                result = await app.AcquireTokenForClient(scopes)
                    .ExecuteAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Token acquired");
                Console.ResetColor();
            }
            catch (MsalServiceException ex) when (ex.Message.Contains("AADSTS70011"))
            {
                // Invalid scope. The scope has to be of the form "https://resourceurl/.default"
                // Mitigation: change the scope to be as expected
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Scope provided is not supported");
                Console.ResetColor();
            }

            if (result != null)
            {
                var builder = new HostBuilder().ConfigureServices((hostContext, services) =>
                {
                    // Create wait and retry policy that will retry up to 3 times and use exponential backoff as default.
                    // However, if Retry-After is provided then that will be used instead
                    IAsyncPolicy<HttpResponseMessage> waitAndRetryPolicy = HttpPolicyExtensions
                                  .HandleTransientHttpError() // HttpRequestException, 5XX and 408
                                  .OrResult(response => (int)response.StatusCode == 429) // Retry-After
                                                                                         //.Or<Exception>()
                                  .WaitAndRetryAsync(
                                    retryCount: 10,
                                    sleepDurationProvider: (retryAttempt, response, context) =>
                                    {
                                        var waitDuration = GetServerWaitDuration(response);

                                        if (waitDuration == TimeSpan.Zero)
                                        {
                                            var retryAfter = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)); //default exponential backoff
                                            return retryAfter;
                                        }
                                        else
                                        {
                                            return waitDuration;
                                        }
                                    },
                                    onRetryAsync: (exception, timespan, retryAttempt, context) =>
                                    {
                                        if (exception.Result.StatusCode != HttpStatusCode.BadRequest)
                                        {

                                        }

                                        Console.ForegroundColor = ConsoleColor.Red;
                                        Console.WriteLine($"Error: {exception.Result.StatusCode} Retry Attempt No: {retryAttempt}, wait duration (sec): {timespan.TotalSeconds}");
                                        Console.ResetColor();

                                        return Task.CompletedTask;
                                    });

                    // Create a noOp policy, so that can be used for idempotent requests
                    IAsyncPolicy<HttpResponseMessage> noOpPolicy = Policy.NoOpAsync().AsAsyncPolicy<HttpResponseMessage>();

                    // Create policy registry and add the policies to it
                    var registry = new PolicyRegistry()
                                    {
                                        { "WaitAndRetryPolicy", waitAndRetryPolicy },
                                        { "NoOpPolicy", noOpPolicy },
                                    };

                    services.AddPolicyRegistry(registry);

                    services.AddHttpClient("D365WebApiClient", client =>
                    {
                        client.BaseAddress = new Uri("https://org.crm.dynamics.com/api/data/v9.2/");
                        client.DefaultRequestHeaders.Add("Accept", "application/json");
                        client.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                        client.DefaultRequestHeaders.Add("OData-Version", "4.0");
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", result.AccessToken);
                        client.Timeout = TimeSpan.FromMinutes(5.0);
                    }).AddPolicyHandlerFromRegistry((policyRegistry, httpRequestMessage) =>
                    {
                        // Use Wait and Retry policy for GET and DELETE only
                        //if (httpRequestMessage.Method == HttpMethod.Get || httpRequestMessage.Method == HttpMethod.Delete)
                        //{
                        return policyRegistry.Get<IAsyncPolicy<HttpResponseMessage>>("WaitAndRetryPolicy");
                        //}

                        // Use NoOp Policy for all other HTTP verbs
                        //return policyRegistry.Get<IAsyncPolicy<HttpResponseMessage>>("NoOpPolicy");
                    });

                    services.AddSingleton<IHostedService, DataverseWebApiService>();
                });

                await builder.RunConsoleAsync();
            }
        }

        /// <summary>
        /// Checks if the sample is configured for using ClientSecret or Certificate. This method is just for the sake of this sample.
        /// You won't need this verification in your production application since you will be authenticating in AAD using one mechanism only.
        /// </summary>
        /// <param name="config">Configuration from appsettings.json</param>
        /// <returns></returns>
        private static bool AppUsesClientSecret(AuthenticationConfig config)
        {
            string clientSecretPlaceholderValue = "[Enter here a client secret for your application]";
            string certificatePlaceholderValue = "[Or instead of client secret: Enter here the name of a certificate (from the user cert store) as registered with your application]";

            if (!String.IsNullOrWhiteSpace(config.ClientSecret) && config.ClientSecret != clientSecretPlaceholderValue)
            {
                return true;
            }

            else if (!String.IsNullOrWhiteSpace(config.CertificateName) && config.CertificateName != certificatePlaceholderValue)
            {
                return false;
            }

            else
                throw new Exception("You must choose between using client secret or certificate. Please update appsettings.json file.");
        }

        private static X509Certificate2 ReadCertificate(string certificateName)
        {
            if (string.IsNullOrWhiteSpace(certificateName))
            {
                throw new ArgumentException("certificateName should not be empty. Please set the CertificateName setting in the appsettings.json", "certificateName");
            }
            X509Certificate2 cert = null;

            using (X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection certCollection = store.Certificates;

                // Find unexpired certificates.
                X509Certificate2Collection currentCerts = certCollection.Find(X509FindType.FindByTimeValid, DateTime.Now, false);

                // From the collection of unexpired certificates, find the ones with the correct name.
                X509Certificate2Collection signingCert = currentCerts.Find(X509FindType.FindBySubjectDistinguishedName, certificateName, false);

                // Return the first certificate in the collection, has the right name and is current.
                cert = signingCert.OfType<X509Certificate2>().OrderByDescending(c => c.NotBefore).FirstOrDefault();
            }
            return cert;
        }

        private static TimeSpan GetServerWaitDuration(DelegateResult<HttpResponseMessage> response)
        {
            var retryAfter = response?.Result?.Headers?.RetryAfter;
            if (retryAfter == null)
                return TimeSpan.Zero;

            return retryAfter.Date.HasValue
                ? retryAfter.Date.Value - DateTime.UtcNow
                : retryAfter.Delta.GetValueOrDefault(TimeSpan.Zero);
        }
    }
}
