using DataverseWebApiPolly.Models;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataverseWebApiPolly
{
    public class DataverseWebApiService : IHostedService
    {
        private IHttpClientFactory _httpClientFactory;

        public DataverseWebApiService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await WhoAmIRequestAsync();
            CreateContacts();
        }

        public void CreateContacts()
        {
            var ttc = new ConcurrentBag<TimeSpan>();
            var contacts = JsonConvert.DeserializeObject<List<Contact>>(File.ReadAllText(@"Contacts.json"));
            var httpClient = _httpClientFactory.CreateClient("D365WebApiClient");

            Parallel.ForEach(contacts, new ParallelOptions { MaxDegreeOfParallelism = 24 },
            (record) =>
            {
                //Create a contact
                var contact = new JObject
                    {
                            {"mxrm_contactid", record.ContactId },
                            {"firstname", record.FirstName },
                            {"lastname", record.LastName },
                            {"emailaddress1", record.EMail },
                            {"telephone1", record.Phone },
                            {"company",record.Company },
                            {"address1_line1", record.Address },
                            {"address1_city", record.City },
                            {"address1_stateorprovince", record.State },
                            {"address1_postalcode", record.ZIP },
                            {"address1_country", record.Country},
                            {"description", record.Description }
                    };

                var jsonData = JsonConvert.SerializeObject(contact);

                var httpContent = new StringContent(jsonData, Encoding.UTF8, "application/json");

                try
                {
                    var sw = new Stopwatch();
                    sw.Start();
                    var response = httpClient.PatchAsync($"contacts(mxrm_contactid={record.ContactId})", httpContent).GetAwaiter().GetResult();

                    if (response.IsSuccessStatusCode)
                    {
                        //string json = await response.Content.ReadAsStringAsync();
                        //JObject result = JsonConvert.DeserializeObject(json) as JObject;
                        //Console.ForegroundColor = ConsoleColor.Gray;
                        //Console.WriteLine($"{record.ContactId} Contact Upserted.");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Failed to call the Web Api: {response.StatusCode}");
                        string content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        // Note that if you got reponse.Code == 403 and reponse.content.code == "Authorization_RequestDenied"
                        // this is because the tenant admin as not granted consent for the application to call the Web API
                        Console.WriteLine($"Content: {content}");
                    }

                    sw.Stop();
                    ttc.Add(sw.Elapsed);
                    var avgSec = ttc.Select(s => s.TotalSeconds).Average();
                    var remaining = contacts.Count - ttc.Count;
                    var estComp = remaining * avgSec;

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Thread Id: {Thread.CurrentThread.ManagedThreadId} Average Upsert Request Time (sec): {avgSec}, completed: {ttc.Count}, remaining: {remaining}, est: {DateTime.Now.AddSeconds(estComp)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            });
        }

        public async Task WhoAmIRequestAsync()
        {
            HttpClient httpClient = _httpClientFactory.CreateClient("D365WebApiClient");
            var response = await httpClient.GetAsync("WhoAmI");

            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                JObject result = JsonConvert.DeserializeObject(json) as JObject;
                Console.ForegroundColor = ConsoleColor.Gray;
                Display(result);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to call the Web Api: {response.StatusCode}");
                string content = await response.Content.ReadAsStringAsync();

                // Note that if you got reponse.Code == 403 and reponse.content.code == "Authorization_RequestDenied"
                // this is because the tenant admin as not granted consent for the application to call the Web API
                Console.WriteLine($"Content: {content}");
            }
        }

        private static void Display(JObject result)
        {
            if (result != null)
            {
                foreach (JProperty child in result.Properties().Where(p => !p.Name.StartsWith("@")))
                {
                    Console.WriteLine($"{child.Name} = {child.Value}");
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
