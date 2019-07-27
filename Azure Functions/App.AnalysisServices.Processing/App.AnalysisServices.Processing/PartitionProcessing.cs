using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

using System.Collections.Generic;
using System.Linq;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Configuration;

namespace App.AnalysisServices.Processing
{
    public static class PartitionProcessing
    {
        [FunctionName("PartitionProcessing")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            // Read the Connection String of hte Analysis Services instance
            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();


            // Read the Query String
            string maxParallelism = req.Query["maxParallelism"];                                // 5
            string ssasFunction = req.Query["ssasFunction"];                                    // "MergePartitions"
            string ssasDatabaseConnectionString = config.GetConnectionString("SqlConnectionString");    // "Data Source=devsqlolap01\tabular;Initial Catalog=CreditRisk_PartitionTest;Provider=MSOLAP.8;Integrated Security=SSPI;Impersonation Level=Impersonate;";
            string ssasDatabaseName = req.Query["ssasDatabaseName"];                            // "CreditRisk_PartitionTest";
            string ssasTablePartitionName = req.Query["ssasTablePartitionName"];                // "Account Settlement~|Transactions~0|Primary Customer~|Term Deposit Renewal~"

            // Read the Request Body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            // Favour the Query String over the Request Body if both are present
            maxParallelism = maxParallelism ?? data?.maxParallelism;
            ssasFunction = ssasFunction ?? data?.ssasFunction;
            ssasDatabaseConnectionString = ssasDatabaseConnectionString ?? data?.ssasDatabaseConnectionString;
            ssasDatabaseName = ssasDatabaseName ?? data?.ssasDatabaseName;
            ssasTablePartitionName = ssasTablePartitionName ?? data?.ssasTablePartitionName;

            string activity = "";
            Server tabularServer = null;

            // Configure the logging framework
            //var logTableConnectionString = ConfigurationManager.AppSettings["LogTableConnectionString"];
            //var colOpts = new ColumnOptions();
            //colOpts.Store.Add(StandardColumn.LogEvent);
            //colOpts.Store.Remove(StandardColumn.Properties);

            //var fred = new LoggerConfiguration()
            //            .WriteTo.MSSqlServer(connectionString: logTableConnectionString
            //                                    , autoCreateSqlTable: true
            //                                    , schemaName: "Logging"
            //                                    , tableName: "Log"
            //                                    , columnOptions: colOpts)
            //            .CreateLogger();

            //log.Logger = fred;

            /**************************************************************************************************************************************************************************
            Partition Convention: Partitions will be named 0 ,1, 2,....n where 0 represents the Current partition, 1 represents the fully completed month prior to Current and so on. 
            **************************************************************************************************************************************************************************/
            try
            {
                activity = $"Arguments received - maxParallelism: {maxParallelism} | ssasFunction: {ssasFunction} | ssasDatabaseConnectionString: {ssasDatabaseConnectionString} | ssasDatabaseName: {ssasDatabaseName} | ssasTablePartitionName: {ssasTablePartitionName}";
                LogActivity(activity, log);

                string ssasTableName = "";
                string ssasPartitionName = "";

                // Connect to the Tabular Model
                tabularServer = new Server();
                tabularServer.Connect(ssasDatabaseConnectionString);

                Database tabularDatabase = tabularServer.Databases[ssasDatabaseName];
                Model tabularModel = tabularDatabase.Model;

                var tablePartitionName = ssasTablePartitionName.Split(new[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries);

                foreach (var tablePartition in tablePartitionName)
                {
                    var tablePartitionCombined = tablePartition.Split(new[] { '~' });
                    ssasTableName = tablePartitionCombined[0];
                    ssasPartitionName = tablePartitionCombined[1] == "" ? null : tablePartitionCombined[1];

                    switch (ssasFunction.ToLower())
                    {
                        case "processfull":
                            activity = $"Process Full - Database {ssasDatabaseName}; Table {ssasTableName} {(ssasPartitionName == null ? "" : "; Partition " + ssasPartitionName)}";
                            LogActivity(activity, log);
                            ProcessFull(tabularModel, ssasTableName, ssasPartitionName);
                            break;

                        case "mergepartitions":
                            activity = $"Merge Partitions - Database {ssasDatabaseName}; Table {ssasTableName}";
                            LogActivity(activity, log);
                            MergePartitions(tabularModel.Tables[ssasTableName]);
                            break;

                        case "shufflepartitions":
                            activity = $"Shuffle Partitions - Database {ssasDatabaseName}; Table {ssasTableName}";
                            LogActivity(activity, log);
                            ShufflePartitions(tabularModel.Tables[ssasTableName]);
                            break;
                    }
                }

                // Send the batch of commands away
                LogActivity("Recalculate Model", log);
                tabularModel.RequestRefresh(RefreshType.Calculate);
                LogActivity("Save Changes to Model", log);
                tabularModel.SaveChanges(new SaveOptions() { MaxParallelism = int.Parse(maxParallelism) });

                // Disconnect from the Server and clean up
                tabularServer.Disconnect();
                tabularServer.Dispose();

                LogActivity("Finished", log);

                //Log.CloseAndFlush();

                // Return success
                return (ActionResult) new OkObjectResult("Success");

                //return name != null
                //    ? (ActionResult)new OkObjectResult($"Hello, {name}")
                //    : new BadRequestObjectResult("Please pass a name on the query string or in the request body");


            }
            catch (Exception ex)
            {
                // Write the error to the StandardError output
                Console.Error.WriteLine(ex.ToString());

                // Write the error to the StandardError output
                Console.WriteLine(ex.ToString());

                LogActivity(ex, "Error", log);

                //Log.CloseAndFlush();

                // Disconnect from the Server and clean up
                tabularServer.Disconnect();
                tabularServer.Dispose();

                // Return failure
                int errorCode = ex.HResult;
                return new BadRequestObjectResult(errorCode == 0 ? -1 : errorCode);
            }
        }

        private static void ProcessFull(Model model, string tableName, string partitionName)
        {
            if (partitionName == null)
            {
                model.Tables[tableName].RequestRefresh(RefreshType.DataOnly);
            }
            else
            {
                model.Tables[tableName].Partitions[partitionName].RequestRefresh(RefreshType.DataOnly);
            }
        }

        private static void MergePartitions(Table table)
        {
            // Create a new Current Partition so we can re-add it later
            QueryPartitionSource currentPartitionSource = table.Partitions["0"].Source as QueryPartitionSource;
            Partition newCurrentPartition = new Partition
            {
                Name = "0",
                Source = new QueryPartitionSource { DataSource = currentPartitionSource.DataSource, Query = currentPartitionSource.Query }
            };

            table.Partitions["1"].RequestMerge(new List<Partition> { table.Partitions["0"] });
            table.Model.SaveChanges();

            // Add the new Current Partition - the old Current Partition is now Merged with Partition "1", Process RECALC to update relationships and save the Model
            table.Partitions.Add(newCurrentPartition);
        }

        private static void ShufflePartitions(Table table)
        {
            // Load the Partition Query for each Partition into a dictionary to use when shuffling Partitions. Find the maximum number of partitions we are dealing with.
            var partitionQueries = table.Partitions.ToDictionary((x) => int.Parse(x.Name), y => (y.Source as QueryPartitionSource).Query);
            int maxPartition = partitionQueries.Keys.Max();

            // If we have already performed the Partition shuffle(i.e. this routine has already been run today), there is nothing to do so exit
            if (table.Partitions["0"].ModifiedTime.ToLocalTime() > DateTime.Today)
            {
                return;
            };

            // Create a new Current Partition so we can re-add it later
            QueryPartitionSource currentPartitionSource = table.Partitions["0"].Source as QueryPartitionSource;
            Partition newCurrentPartition = new Partition
            {
                Name = "0",
                Source = new QueryPartitionSource { DataSource = currentPartitionSource.DataSource, Query = currentPartitionSource.Query }
            };

            // Delete the oldest Partition and save the model
            table.Partitions.Remove($"{maxPartition}");
            table.Model.SaveChanges();

            // Now iterate over all the Partitions from the second oldest to the newest to rename them to the next oldest
            for (int i = (maxPartition - 1); i >= 0; i--)
            {
                ((QueryPartitionSource)table.Partitions[i.ToString()].Source).Query = partitionQueries[i + 1];
                table.Partitions[i.ToString()].RequestRename((i + 1).ToString());
                table.Model.SaveChanges();
            }

            // Add the new Current Partition - the old Current Partition is now named "1", Process RECALC to update relationships and save the Model
            table.Partitions.Add(newCurrentPartition);
        }

        private static void LogActivity(string logMessage, ILogger log)
        {
            LogActivity(null, logMessage, log);
        }

        private static void LogActivity(Exception ex, string logMessage, ILogger log)
        {
            const string system = "Model Processing";
            var logText = string.Concat("{System}: ", logMessage);

            if (ex == null)
            {
                log.LogInformation(logText, system);
            }
            else
            {
                log.LogError(ex, logText, system);
            }
        }
    }
}
