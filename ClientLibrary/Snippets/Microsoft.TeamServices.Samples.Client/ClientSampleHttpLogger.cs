﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.TeamServices.Samples.Client
{
    public class ClientSampleHttpLogger : DelegatingHandler
    {
        public static readonly string PropertyOutputFilePath = "$outputFilePath";   // value is a string indicating the folder to output files to
        public static readonly string PropertySuppressOutput = "$suppressOutput";   // value is a boolan indicating whether to suppress output
        //public static readonly string PropertyOutputToConsole = "$outputToConsole"; // value is a boolan indicating whether to output JSON to the console
        public static readonly string PropertyOperationName = "$operationName";   // value is a string indicating the logical name of the operation. If output is enabled, this value is used to produce the output file name.

        private JsonSerializerSettings serializerSettings;

        private static HashSet<string> s_excludedHeaders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
        {
            "x-VSS-PerfData",
            "x-TFS-Session",
            "x-VSS-E2EID",
            "x-VSS-Agent",
            "authorization",
            "x-TFS-ProcessId",
            "x-VSS-UserData",
            "activityId",
            "p3P",
            "x-Powered-By",
            "cookie",
            "x-TFS-FedAuthRedirect",
            "strict-Transport-Security",
            "x-FRAME-OPTIONS",
            "x-Content-Type-Options",
            "x-AspNet-Version",
            "server",
            "pragma",
            "vary",
            "x-MSEdge-Ref",
            "cache-Control",
            "date",
            "user-Agent",
            "accept-Language"
        };

        private static HashSet<string> s_combinableHeaders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
        {
            "user-Agent"
        };

        public ClientSampleHttpLogger()
        {
            serializerSettings = new JsonSerializerSettings();

            serializerSettings.Formatting = Formatting.Indented;
            serializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);

            RunnableClientSampleMethod runnableMethod = ClientSampleContext.CurrentRunnableMethod;

            if (runnableMethod != null)
            {
                bool suppressOutput;
                if (!ClientSampleContext.CurrentContext.TryGetValue<bool>(PropertySuppressOutput, out suppressOutput))
                {
                    suppressOutput = false;
                }

                string operationName;
                if (!ClientSampleContext.CurrentContext.TryGetValue<string>(PropertyOperationName, out operationName))
                {
                    operationName = ClientSampleContext.CurrentRunnableMethod.MethodBase.Name;
                }
                else
                {
                    // TODO: add validation around the operation name
                }

                if (!suppressOutput)
                {
                    DirectoryInfo baseOutputPath;
                    if (ClientSampleContext.CurrentContext.TryGetValue<DirectoryInfo>(PropertyOutputFilePath, out baseOutputPath))
                    {
                        Dictionary<string, object> requestHeaders = ProcessHeaders(request.Headers);
                        Dictionary<string, object> responseHeaders = ProcessHeaders(response.Headers);

                        dynamic requestBody = null;
                        try
                        {
                            string requestBodyString = await request.Content.ReadAsStringAsync();
                            if (!String.IsNullOrEmpty(requestBodyString))
                            {
                                requestBody = JValue.Parse(requestBodyString);
                            }
                        }
                        catch (Exception) { }

                        JObject responseBody = null;
                        try
                        {
                            if (IsJsonResponse(response))
                            {
                                string responseBodyString = await response.Content.ReadAsStringAsync();
                                responseBody = JObject.Parse(responseBodyString);
                            }
                        }
                        catch (Exception) { }

                        ApiResponseMetadata responseData = new ApiResponseMetadata()
                        {
                            Body = responseBody,
                            Headers = responseHeaders
                        };

                        Dictionary<string, object> requestParameters = new Dictionary<string, object>
                        {
                            { "body", requestBody }
                        };
                        foreach (var rh in requestHeaders)
                        {
                            requestParameters.Add(rh.Key, rh.Value);
                        }

                        if (!requestParameters.ContainsKey("account"))
                        {
                            requestParameters["account"] = "fabrikam";
                        }

                        ApiRequestResponseMetdata data = new ApiRequestResponseMetdata()
                        {
                            Area = runnableMethod.Area,
                            Resource = runnableMethod.Resource,
                            HttpMethod = request.Method.ToString().ToUpperInvariant(),
                            RequestUrl = request.RequestUri.ToString(),
                            Parameters = requestParameters,
                            Responses = new Dictionary<string, ApiResponseMetadata>()
                            {
                                {  ((int)response.StatusCode).ToString(), responseData }
                            },                            
                            Generated = true      
                        };

                        string outputPath = Path.Combine(baseOutputPath.FullName, char.ToLower(data.Area[0]) + data.Area.Substring(1), char.ToLower(data.Resource[0]) + data.Resource.Substring(1));
                        string outputFileName = operationName + ".json";

                        DirectoryInfo outputDirectory = Directory.CreateDirectory(outputPath);

                        string outputFile = Path.Combine(outputDirectory.FullName, outputFileName);

                        string output = JsonConvert.SerializeObject(data, this.serializerSettings);

                        File.WriteAllText(outputFile, output);
                    }
                }
            }

            return response;
        }

        private static Dictionary<string,object> ProcessHeaders(HttpHeaders headers)
        {
            Dictionary<string, object> ret = new Dictionary<string, object>();

            foreach (var h in headers.Where(kvp => { return !s_excludedHeaders.Contains(kvp.Key); }))
            {
                if (h.Value.Count() == 1)
                {
                    ret[h.Key] = h.Value.First();
                }
                else
                {
                    if (s_combinableHeaders.Contains(h.Key))
                    {
                        ret[h.Key] = String.Join(" ", h.Value);
                    }
                    else
                    {
                        ret[h.Key] = h.Value;
                    }
                }
            }

            return ret;
        }

        private static bool ResponseHasContent(HttpResponseMessage response)
        {
            if (response != null &&
                response.StatusCode != HttpStatusCode.NoContent &&
                response.Content != null &&
                response.Content.Headers != null &&
                (!response.Content.Headers.ContentLength.HasValue ||
                 (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != 0)))
            {
                return true;
            }

            return false;
        }

        private static bool IsJsonResponse(HttpResponseMessage response)
        {
            if (ResponseHasContent(response)
                && response.Content.Headers != null && response.Content.Headers.ContentType != null
                && !String.IsNullOrEmpty(response.Content.Headers.ContentType.MediaType))
            {
                return (0 == String.Compare("application/json", response.Content.Headers.ContentType.MediaType, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        public static void SetSuppressOutput(ClientSampleContext context, bool suppress)
        {
            context.SetValue<bool>(PropertySuppressOutput, suppress);
        }

        public static void SetOperationName(ClientSampleContext context, string name)
        {
            context.SetValue<string>(PropertyOperationName, name);
        }

        public static void ResetOperationName(ClientSampleContext context)
        {
            context.RemoveValue(PropertyOperationName);
        }
    }

    public class ClientSampleHttpLoggerOutputSuppression : IDisposable
    {
        public ClientSampleHttpLoggerOutputSuppression()
        {
            ClientSampleHttpLogger.SetSuppressOutput(ClientSampleContext.CurrentContext, true);
        }

        public void Dispose()
        {
            ClientSampleHttpLogger.SetSuppressOutput(ClientSampleContext.CurrentContext, false);
        }
    }

    [DataContract]
    class ApiRequestResponseMetdata : ClientSampleMethodInfo
    {
        [DataMember(Name = "x-vss-request-method")]
        public String HttpMethod;

        [DataMember(Name = "x-vss-request-url")]
        public String RequestUrl;

        [DataMember]
        public Dictionary<string, object> Parameters;

        [DataMember]
        public Dictionary<string, ApiResponseMetadata> Responses;

        [DataMember(Name = "x-vss-generated")]
        public bool Generated;

        [DataMember(Name = "x-vss-format")]
        public int Format { get { return 1; } }
    }

    [DataContract]
    class ApiResponseMetadata
    {
        [DataMember]
        public Dictionary<String, object> Headers;

        [DataMember(EmitDefaultValue = false)]
        public Object Body;
    }
}