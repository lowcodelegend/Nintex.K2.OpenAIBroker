using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SourceCode.SmartObjects.Services.ServiceSDK;
using SourceCode.SmartObjects.Services.ServiceSDK.Objects;
using SourceCode.SmartObjects.Services.ServiceSDK.Types;
using LiteDB;

namespace Nintex.K2
{
    public class OpenAIBroker : ServiceAssemblyBase
    {
        // Constants for Service Keys
        private const string CONFIG_OPENAI_KEY = "OpenAIKey";
        private const string CONFIG_OPENAI_SYSTEM_PROMPT = "OpenAISystemPrompt";
        private const string CONFIG_OPENAI_MODEL = "OpenAIModel";
        private const string CONFIG_JSON_PROPERTIES = "JsonPropertiesToExtract"; // Comma-separated list
        private const string CONFIG_USE_LITEDB_CACHE = "UseLiteDBCache";
        private const string CONFIG_CACHE_DURATION = "CacheDuration";
        private const string CONFIG_RETURN_AS_LIST = "ReturnAsList";
        private const string CONFIG_OPENAI_ENDPOINT = "OpenAIEndpoint";
        private const string CONFIG_CHAT_TOPIC = "Chat Topic";

        // Method Names
        private const string METHOD_GET_RESPONSE = "GetResponse";
        private const string METHOD_INVALIDATE_CACHE = "InvalidateCache";

        public override string GetConfigSection()
        {
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_KEY, true, "Your OpenAI API Key");
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_SYSTEM_PROMPT, true, "ONLY return minified JSON to use the least tokens possible.");
            this.Service.ServiceConfiguration.Add(CONFIG_CHAT_TOPIC, true, "e.g. Leave Request Policy");
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_MODEL, true, "gpt-4-turbo");
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_ENDPOINT, true, "https://api.openai.com/v1/chat/completions");
            this.Service.ServiceConfiguration.Add(CONFIG_JSON_PROPERTIES, true, "Comma-separated list of JSON properties to return from inside the response.choice[0].content object e.g. id,Message[0].item.");
            this.Service.ServiceConfiguration.Add(CONFIG_USE_LITEDB_CACHE, true, "true");
            this.Service.ServiceConfiguration.Add(CONFIG_CACHE_DURATION, true, "1yr");
            this.Service.ServiceConfiguration.Add(CONFIG_RETURN_AS_LIST, true, "false");

            return base.GetConfigSection();
        }

        public override string DescribeSchema()
        {
            // Use the Service Instance Name to name the service and objects
            string serviceInstanceName = this.Service.Name;
            this.Service.Name = serviceInstanceName;
            this.Service.MetaData.DisplayName = serviceInstanceName;

            // Retrieve ReturnAsList setting
            bool returnAsList = false;
            bool.TryParse(this.Service.ServiceConfiguration[CONFIG_RETURN_AS_LIST].ToString(), out returnAsList);


            // Create the Service Object dynamically
            var so = new ServiceObject();
            // Name the service object and smartobject after the instance name for uniqueness
            so.Name = serviceInstanceName + "_OpnAIResp";
            so.MetaData.DisplayName = serviceInstanceName;
            so.Active = true;

            // "GetResponse" method
            var getResponseMethod = new Method();
            getResponseMethod.Name = METHOD_GET_RESPONSE;
            getResponseMethod.MetaData.DisplayName = "Get OpenAI Response";
            getResponseMethod.Type = returnAsList ? MethodType.List : MethodType.Read;

            // Input property: User Prompt
            var userPromptProperty = new Property("UserPrompt");
            userPromptProperty.Type = "Memo";
            userPromptProperty.MetaData.DisplayName = "User Prompt";
            so.Properties.Add(userPromptProperty);
            getResponseMethod.InputProperties.Add(userPromptProperty);

            // Input property: RefreshCache (boolean)
            var refreshCacheProperty = new Property("RefreshCache");
            refreshCacheProperty.Type = "Boolean";
            refreshCacheProperty.MetaData.DisplayName = "Refresh Cache";
            refreshCacheProperty.Value = "false"; // default to false
            so.Properties.Add(refreshCacheProperty);
            getResponseMethod.InputProperties.Add(refreshCacheProperty);

            // Always return the full response
            var fullResponseProp = new Property("FullResponse");
            fullResponseProp.Type = "Memo";
            fullResponseProp.MetaData.DisplayName = "Full Response";
            so.Properties.Add(fullResponseProp);
            getResponseMethod.ReturnProperties.Add(fullResponseProp);

            // Create properties from the CSV config
            var jsonProps = GetJsonPropertiesFromConfig();
            foreach (var propName in jsonProps)
            {
                var p = new Property(propName);
                p.Type = "Text";
                p.MetaData.DisplayName = propName;
                so.Properties.Add(p);
                getResponseMethod.ReturnProperties.Add(p);
            }

            so.Methods.Add(getResponseMethod);

            // Add InvalidateCache method (no inputs)
            var invalidateCacheMethod = new Method();
            invalidateCacheMethod.Name = METHOD_INVALIDATE_CACHE;
            invalidateCacheMethod.MetaData.DisplayName = "Invalidate Cache";
            invalidateCacheMethod.Type = MethodType.Execute;
            so.Methods.Add(invalidateCacheMethod);

            this.Service.ServiceObjects.Create(so);
            ServicePackage.IsSuccessful = true;
            return base.DescribeSchema();
        }

        public override void Execute()
        {
            string apiKey = this.Service.ServiceConfiguration[CONFIG_OPENAI_KEY].ToString();
            string systemPrompt = this.Service.ServiceConfiguration[CONFIG_OPENAI_SYSTEM_PROMPT].ToString();
            systemPrompt += "\nDecline to responsd and give reason if the request is not on the topic: " + this.Service.ServiceConfiguration[CONFIG_CHAT_TOPIC].ToString();
            string model = this.Service.ServiceConfiguration[CONFIG_OPENAI_MODEL].ToString();
            string endpointUrl = this.Service.ServiceConfiguration[CONFIG_OPENAI_ENDPOINT].ToString();
            var jsonProps = GetJsonPropertiesFromConfig();

            bool useCache = false;
            bool.TryParse(this.Service.ServiceConfiguration[CONFIG_USE_LITEDB_CACHE]?.ToString(), out useCache);

            string cacheDurationStr = this.Service.ServiceConfiguration[CONFIG_CACHE_DURATION]?.ToString() ?? "1day";
            TimeSpan cacheDuration = ParseDuration(cacheDurationStr);

            bool returnAsList = false;
            bool.TryParse(this.Service.ServiceConfiguration[CONFIG_RETURN_AS_LIST]?.ToString(), out returnAsList);

            ServiceObject serviceObject = Service.ServiceObjects[0];
            var currentMethod = serviceObject.Methods[0];

            Property[] inputs = currentMethod.InputProperties.Select(ip => serviceObject.Properties[ip]).ToArray();
            Property[] outputs = currentMethod.ReturnProperties.Select(rp => serviceObject.Properties[rp]).ToArray();

            switch (currentMethod.Name)
            {
                case METHOD_GET_RESPONSE:
                    {
                        string userPrompt = GetInputValue(inputs, "UserPrompt");
                        bool refreshCache = false;
                        bool.TryParse(GetInputValue(inputs, "RefreshCache"), out refreshCache);

                        string cachedResponse = null;

                        if (useCache && !refreshCache)
                        {
                            cachedResponse = GetCachedResponse(userPrompt);
                        }

                        string finalContentJson;
                        if (!string.IsNullOrEmpty(cachedResponse))
                        {
                            // Use cached response
                            finalContentJson = cachedResponse;
                            if (returnAsList)
                            {
                                // Handle as list
                                HandleListResponse(finalContentJson, jsonProps, outputs, serviceObject);
                            }
                            else
                            {
                                // Single response
                                serviceObject.Properties.InitResultTable();
                                SetOutputValue(outputs, "FullResponse", finalContentJson);

                                try
                                {
                                    JObject innerJson = JObject.Parse(finalContentJson);
                                    foreach (var propName in jsonProps)
                                    {
                                        JToken valToken = innerJson.SelectToken(propName);
                                        string val = valToken != null ? valToken.ToString() : string.Empty;
                                        SetOutputValue(outputs, propName, val);
                                    }
                                }
                                catch (JsonReaderException)
                                {
                                    // Not valid JSON - no properties extracted
                                }

                                serviceObject.Properties.BindPropertiesToResultTable();
                            }
                        }
                        else
                        {
                            // Call the OpenAI API
                            var responseJson = CallOpenAIAPI(apiKey, systemPrompt, model, userPrompt, endpointUrl);

                            // Parse response to get content
                            JObject jsonObj;
                            string contentString = null;
                            try
                            {
                                jsonObj = JObject.Parse(responseJson);
                                JToken contentToken = jsonObj.SelectToken("choices[0].message.content");
                                contentString = contentToken != null ? contentToken.ToString() : string.Empty;
                            }
                            catch (JsonReaderException)
                            {
                                // Could not parse top-level, just return raw
                                serviceObject.Properties.InitResultTable();
                                SetOutputValue(outputs, "FullResponse", responseJson);
                                serviceObject.Properties.BindPropertiesToResultTable();
                                return;
                            }

                            finalContentJson = contentString;

                            // If caching is enabled and not explicitly refreshing, store the result
                            if (useCache && !string.IsNullOrEmpty(finalContentJson))
                            {
                                CacheResponse(userPrompt, finalContentJson, cacheDuration);
                            }

                            if (returnAsList)
                            {
                                // Handle as list
                                HandleListResponse(finalContentJson, jsonProps, outputs, serviceObject);
                            }
                            else
                            {
                                // Single row
                                serviceObject.Properties.InitResultTable();
                                SetOutputValue(outputs, "FullResponse", finalContentJson);

                                try
                                {
                                    JObject innerJson = JObject.Parse(finalContentJson);
                                    foreach (var propName in jsonProps)
                                    {
                                        JToken valToken = innerJson.SelectToken(propName);
                                        string val = valToken != null ? valToken.ToString() : string.Empty;
                                        SetOutputValue(outputs, propName, val);
                                    }
                                }
                                catch (JsonReaderException)
                                {
                                    // The content wasn't valid JSON, no properties extracted
                                }
                                serviceObject.Properties.BindPropertiesToResultTable();
                            }
                        }
                        break;
                    }
                case METHOD_INVALIDATE_CACHE:
                    {
                        // Clear entire cache for this service instance
                        InvalidateAllCacheEntries();
                        // No output, just return
                        break;
                    }
            }
        }

        private void HandleListResponse(string finalContentJson, List<string> jsonProps, Property[] outputs, ServiceObject so)
        {
            // Attempt to parse the finalContentJson as a JSON array
            // If it's an array, for each element, output a row
            // If not an array, just return one row

            JToken parsedToken;
            try
            {
                parsedToken = JToken.Parse(finalContentJson);
            }
            catch (JsonReaderException)
            {
                // Not a valid JSON at all, just return one row with FullResponse
                so.Properties.InitResultTable();
                SetOutputValue(outputs, "FullResponse", finalContentJson);
                so.Properties.BindPropertiesToResultTable();
                return;
            }

            if (parsedToken.Type == JTokenType.Array)
            {
                JArray arr = (JArray)parsedToken;
                so.Properties.InitResultTable();
                foreach (var element in arr)
                {
                    SetOutputValue(outputs, "FullResponse", element.ToString());

                    if (element.Type == JTokenType.Object)
                    {
                        JObject objElement = (JObject)element;
                        foreach (var propName in jsonProps)
                        {
                            JToken valToken = objElement.SelectToken(propName);
                            string val = valToken != null ? valToken.ToString() : string.Empty;
                            SetOutputValue(outputs, propName, val);
                        }
                    }
                    so.Properties.BindPropertiesToResultTable();
                }

            }
            else
            {
                // Not an array, treat as single row
                so.Properties.InitResultTable();
                SetOutputValue(outputs, "FullResponse", finalContentJson);

                if (parsedToken.Type == JTokenType.Object)
                {
                    JObject innerJson = (JObject)parsedToken;
                    foreach (var propName in jsonProps)
                    {
                        JToken valToken = innerJson.SelectToken(propName);
                        string val = valToken != null ? valToken.ToString() : string.Empty;
                        SetOutputValue(outputs, propName, val);
                    }
                }

                so.Properties.BindPropertiesToResultTable();
            }
        }

        public List<string> GetJsonPropertiesFromConfig()
        {
            var props = new List<string>();

            string raw = this.Service.ServiceConfiguration[CONFIG_JSON_PROPERTIES]?.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                props = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(p => p.Trim())
                           .ToList();
            }

            return props;
        }

        private string GetInputValue(Property[] inputs, string propName)
        {
            var prop = inputs.FirstOrDefault(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));
            if (prop != null && prop.Value != null) return prop.Value.ToString();
            return string.Empty;
        }

        private void SetOutputValue(Property[] outputs, string propName, string value)
        {
            var prop = outputs.FirstOrDefault(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));
            if (prop != null)
            {
                prop.Value = value;
            }
        }

        private string CallOpenAIAPI(string apiKey, string systemPrompt, string model, string userPrompt, string endpointUrl)
        {
            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };

            string requestJson = JsonConvert.SerializeObject(requestBody);

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                client.DefaultRequestHeaders.Add("User-Agent", "K2OpenAIBroker/1.0");

                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                var response = client.PostAsync(endpointUrl, content).Result;
                response.EnsureSuccessStatusCode();

                var responseContent = response.Content.ReadAsStringAsync().Result;
                return responseContent;
            }
        }

        // LiteDB cache logic
        private class CacheEntry
        {
            public string Id { get; set; } // key: userPrompt
            public string Response { get; set; }
            public DateTime ExpiresAt { get; set; }
        }

        private string GetDbFilePath()
        {
            // Unique DB file per service instance
            string serviceInstanceName = this.Service.Name;
            return $"OpenAICache_{serviceInstanceName}.db";
        }

        private string GetCachedResponse(string userPrompt)
        {
            using (var db = new LiteDatabase($"filename={GetDbFilePath()};mode=Shared"))
            {
                var col = db.GetCollection<CacheEntry>("cache");
                var entry = col.FindById(userPrompt);
                if (entry != null && entry.ExpiresAt > DateTime.UtcNow)
                {
                    return entry.Response;
                }
                else if (entry != null && entry.ExpiresAt <= DateTime.UtcNow)
                {
                    // expired, remove it
                    col.Delete(userPrompt);
                }
            }
            return null;
        }

        private void CacheResponse(string userPrompt, string response, TimeSpan duration)
        {
            using (var db = new LiteDatabase($"filename={GetDbFilePath()};mode=Shared"))
            {
                var col = db.GetCollection<CacheEntry>("cache");
                var entry = new CacheEntry
                {
                    Id = userPrompt,
                    Response = response,
                    ExpiresAt = DateTime.UtcNow.Add(duration)
                };
                col.Upsert(entry);
            }
        }

        private void InvalidateAllCacheEntries()
        {
            using (var db = new LiteDatabase($"filename={GetDbFilePath()};mode=Shared"))
            {
                var col = db.GetCollection<CacheEntry>("cache");
                col.DeleteAll();
            }
        }

        private TimeSpan ParseDuration(string durationStr)
        {
            // Very basic parser for formats like 1sec, 1min, 1day, 1mon, 1yr
            if (string.IsNullOrWhiteSpace(durationStr))
            {
                return TimeSpan.FromDays(1);
            }

            durationStr = durationStr.Trim().ToLower();
            if (durationStr.EndsWith("sec"))
            {
                int val = ParseNumber(durationStr, "sec");
                return TimeSpan.FromSeconds(val);
            }
            else if (durationStr.EndsWith("min"))
            {
                int val = ParseNumber(durationStr, "min");
                return TimeSpan.FromMinutes(val);
            }
            else if (durationStr.EndsWith("day"))
            {
                int val = ParseNumber(durationStr, "day");
                return TimeSpan.FromDays(val);
            }
            else if (durationStr.EndsWith("mon"))
            {
                int val = ParseNumber(durationStr, "mon");
                // approx 30 days
                return TimeSpan.FromDays(30 * val);
            }
            else if (durationStr.EndsWith("yr"))
            {
                int val = ParseNumber(durationStr, "yr");
                // approx 365 days
                return TimeSpan.FromDays(365 * val);
            }

            // default to 1 day if unable to parse
            return TimeSpan.FromDays(1);
        }

        private int ParseNumber(string str, string suffix)
        {
            var numberPart = str.Replace(suffix, "");
            if (int.TryParse(numberPart, out int val))
            {
                return val;
            }
            return 1;
        }

        public override void Extend()
        {
            throw new NotImplementedException();
        }
    }
}
