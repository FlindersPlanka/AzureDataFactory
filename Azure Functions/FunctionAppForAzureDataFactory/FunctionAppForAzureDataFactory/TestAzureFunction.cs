using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;

using Microsoft.AspNetCore.Mvc;

namespace FunctionAppForAzureDataFactory
{
    public static class TestAzureFunction
    {
        [FunctionName("TestAzureFunction")]
        public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            log.Info("C# HTTP trigger function processed a request.");

            // parse query parameter
            string name = req.GetQueryNameValuePairs()
                .FirstOrDefault(q => string.Compare(q.Key, "name", true) == 0)
                .Value;

            if (name == null)
            {
                // Get request body
                dynamic data = await req.Content.ReadAsAsync<object>();
                name = data?.name;
            }

            //return new OkObjectResult(new[] { new { hello = "Mick", Variables = new { SprocToExecute = "Fred", EntityName = "Bob" } },
            //                                  new { hello = "Wood", Variables = new { SprocToExecute = "John", EntityName = "Ralph" } }});//jsonObject);

            return name == null
                ? new BadRequestObjectResult("Please pass a name on the query string or in the request body")
                : (ActionResult)new OkObjectResult(new[] { new { hello = name, Variables = new { SprocToExecute = "Fred", EntityName = "Bob" } },
                                                    new { hello = name, Variables = new { SprocToExecute = "John", EntityName = "Ralph" } }});
        }
    }
}
