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
        private const string CONFIG_INPUT_PROPS_LIST = "InputPropertiesList"; // Comma-separated list e.g. prompt, or job,city,currency
        private const string CONFIG_RETURN_PROPS_LIST = "ReturnPropertiesList"; // Comma-separated list
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
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_SYSTEM_PROMPT, true, "Describe the purpose like: validate if the request meets the policy, give reasons as to why or why not.");
            this.Service.ServiceConfiguration.Add(CONFIG_CHAT_TOPIC, true, "e.g. Leave Request Policy");
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_MODEL, true, "gpt-4o");
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_ENDPOINT, true, "https://api.openai.com/v1/chat/completions");
            this.Service.ServiceConfiguration.Add(CONFIG_INPUT_PROPS_LIST, true, "e.g. prompt or requestType,requestStartate,requestEndDate,policy,currentDate, requestedLeaveType, additionalConsiderations");
            this.Service.ServiceConfiguration.Add(CONFIG_RETURN_PROPS_LIST, true, "e.g. response or meetsPolicy,reason");
            this.Service.ServiceConfiguration.Add(CONFIG_USE_LITEDB_CACHE, true, "true");
            this.Service.ServiceConfiguration.Add(CONFIG_CACHE_DURATION, true, "1yr");
            this.Service.ServiceConfiguration.Add(CONFIG_RETURN_AS_LIST, true, "false");

            return base.GetConfigSection();
        }

        public override string DescribeSchema()
        {
            string serviceInstanceName = this.Service.Name;
            this.Service.Name = serviceInstanceName;
            this.Service.MetaData.DisplayName = serviceInstanceName;

            bool returnAsList = false;
            bool.TryParse(this.Service.ServiceConfiguration[CONFIG_RETURN_AS_LIST].ToString(), out returnAsList);
            string inputPropsList = this.Service.ServiceConfiguration[CONFIG_INPUT_PROPS_LIST].ToString();
            string returnPropsList = this.Service.ServiceConfiguration[CONFIG_RETURN_PROPS_LIST].ToString();

            var so = new ServiceObject();
            so.Name = serviceInstanceName + "_OpnAIResp";
            so.MetaData.DisplayName = serviceInstanceName;
            so.Active = true;

            var getResponseMethod = new Method();
            getResponseMethod.Name = METHOD_GET_RESPONSE;
            getResponseMethod.MetaData.DisplayName = "Get OpenAI Response";
            getResponseMethod.Type = returnAsList ? MethodType.List : MethodType.Read;

            // Input props
            var inputProps = GetJsonPropertiesFromConfig(inputPropsList);
            foreach (var propName in inputProps)
            {
                var p = new Property(propName);
                p.Type = "Text";
                p.MetaData.DisplayName = propName;
                so.Properties.Add(p);
                getResponseMethod.InputProperties.Add(p);
            }

            //Cache Refresh property
            var refreshCacheProperty = new Property("RefreshCache");
            refreshCacheProperty.Type = "Boolean";
            refreshCacheProperty.MetaData.DisplayName = "Refresh Cache";
            refreshCacheProperty.Value = "false"; // default to false
            so.Properties.Add(refreshCacheProperty);
            getResponseMethod.InputProperties.Add(refreshCacheProperty);

            //Input file
            var fileAttachment = new Property("FileAttachement");
            fileAttachment.SoType = SoType.File;
            fileAttachment.MetaData.DisplayName = "File Attachment";
            fileAttachment.MetaData.Description = "See the documentation!  pdf, docx, jpg, png, csv xlsx.  20Mb, 2000px x 768px max";
            so.Properties.Add(fileAttachment);
            getResponseMethod.InputProperties.Add(fileAttachment);

            // Always return the full response
            var fullResponseProp = new Property("FullResponse");
            fullResponseProp.Type = "Memo";
            fullResponseProp.MetaData.DisplayName = "Full Response";
            so.Properties.Add(fullResponseProp);
            getResponseMethod.ReturnProperties.Add(fullResponseProp);

            // Return props
            var returnProps = GetJsonPropertiesFromConfig(returnPropsList);
            foreach (var propName in returnProps)
            {
                var p = new Property(propName);
                p.Type = "Text";
                p.MetaData.DisplayName = propName;
                so.Properties.Add(p);
                getResponseMethod.ReturnProperties.Add(p);
            }

            so.Methods.Add(getResponseMethod);

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
            string model = this.Service.ServiceConfiguration[CONFIG_OPENAI_MODEL].ToString();
            string endpointUrl = this.Service.ServiceConfiguration[CONFIG_OPENAI_ENDPOINT].ToString();
            var inputPropsList = GetJsonPropertiesFromConfig(this.Service.ServiceConfiguration[CONFIG_INPUT_PROPS_LIST].ToString());
            var inputPropsJson = ParseCSVToJson(this.Service.ServiceConfiguration[CONFIG_INPUT_PROPS_LIST].ToString());
            var returnPropsList = GetJsonPropertiesFromConfig(this.Service.ServiceConfiguration[CONFIG_RETURN_PROPS_LIST].ToString());
            var ReturnListjson = ParseCSVToJson(this.Service.ServiceConfiguration[CONFIG_RETURN_PROPS_LIST].ToString());

            var useCache = false;
            bool.TryParse(this.Service.ServiceConfiguration[CONFIG_USE_LITEDB_CACHE]?.ToString(), out useCache);

            string cacheDurationStr = this.Service.ServiceConfiguration[CONFIG_CACHE_DURATION]?.ToString() ?? "1day";
            TimeSpan cacheDuration = ParseDuration(cacheDurationStr);

            bool returnAsList = false;
            bool.TryParse(this.Service.ServiceConfiguration[CONFIG_RETURN_AS_LIST]?.ToString(), out returnAsList);

            systemPrompt += "\nDecline to responsd and give reason if the request is not on the topic: "
                + this.Service.ServiceConfiguration[CONFIG_CHAT_TOPIC].ToString();
            systemPrompt += "\nReturn only JSON.  Return the JSON minified to save tokens.";
            if (returnAsList)
            {
                systemPrompt += $"\nReturn as JSON array of the return list object: [{ReturnListjson}]";
            }
            else
            {
                systemPrompt += $"\nReturn a single record JSON object like this: {ReturnListjson}";
            }

            ServiceObject serviceObject = Service.ServiceObjects[0];
            var currentMethod = serviceObject.Methods[0];

            Property[] inputs = currentMethod.InputProperties.Select(ip => serviceObject.Properties[ip]).ToArray();
            Property[] outputs = currentMethod.ReturnProperties.Select(rp => serviceObject.Properties[rp]).ToArray();

            switch (currentMethod.Name)
            {
                case METHOD_GET_RESPONSE:
                    {
                        var userPrompt = string.Empty;
                        var cachedResponse = string.Empty;
                        var refreshCache = false;

                        bool.TryParse(GetInputValue(inputs, "RefreshCache"), out refreshCache);

                        // Map input properties in SMO to userPrompt.
                        userPrompt = MapInputPropertiesToJson(inputPropsJson, inputs);

                        // --- File Attachment Processing ---
                        // Retrieve the file attachment XML (if any)
                        string fileAttachmentXml = GetInputValue(inputs, "FileAttachement");
                        // Initialize an attachments dictionary for base64 image URIs.
                        Dictionary<string, string> imageAttachments = null;
                        if (!string.IsNullOrWhiteSpace(fileAttachmentXml))
                        {
                            // Parse the XML fragment.
                            string fileAttachmentFilename = string.Empty;
                            string fileAttachmentContent = string.Empty;
                            try
                            {
                                var xmlDoc = System.Xml.Linq.XDocument.Parse(fileAttachmentXml);
                                var fileElement = xmlDoc.Element("file");
                                if (fileElement != null)
                                {
                                    fileAttachmentFilename = fileElement.Element("name")?.Value;
                                    fileAttachmentContent = fileElement.Element("content")?.Value;
                                }
                            }
                            catch (Exception ex)
                            {
                                throw new Exception("Error parsing FileAttachement XML: " + ex.Message, ex);
                            }
                            if (string.IsNullOrWhiteSpace(fileAttachmentFilename) || string.IsNullOrWhiteSpace(fileAttachmentContent))
                            {
                                throw new Exception("FileAttachement XML is missing the Name or Content element.");
                            }
                            try
                            {
                                // Process the file attachment using DocumentProcessor.
                                var dpResult = DocumentProcessing.ProcessFile(fileAttachmentFilename, fileAttachmentContent);
                                // Append the processed text (which includes placeholders) to the user prompt.
                                userPrompt += "\nAttached Document: " + dpResult.ProcessedText;
                                imageAttachments = dpResult.ImageAttachments;
                                // Optionally: you can log dpResult.TokenCount or enforce a token limit here.
                            }
                            catch (Exception ex)
                            {
                                throw new Exception("Error processing file attachment: " + ex.Message, ex);
                            }
                        }
                        // --- End File Attachment Processing ---

                        // Check the cache if enabled and no refresh is requested
                        if (useCache && !refreshCache)
                        {
                            cachedResponse = GetCachedResponse(userPrompt);
                        }

                        string finalContentJson;
                        if (!string.IsNullOrEmpty(cachedResponse))
                        {
                            // Use cached response.
                            finalContentJson = cachedResponse;
                            if (returnAsList)
                            {
                                // Handle as list.
                                HandleListResponse(finalContentJson, returnPropsList, outputs, serviceObject);
                            }
                            else
                            {
                                // Single response.
                                serviceObject.Properties.InitResultTable();
                                SetOutputValue(outputs, "FullResponse", finalContentJson);
                                try
                                {
                                    JObject innerJson = JObject.Parse(finalContentJson);
                                    foreach (var propName in returnPropsList)
                                    {
                                        JToken valToken = innerJson.SelectToken(propName);
                                        string val = valToken != null ? valToken.ToString() : string.Empty;
                                        SetOutputValue(outputs, propName, val);
                                    }
                                }
                                catch (JsonReaderException)
                                {
                                    // Not valid JSON - no properties extracted.
                                }
                                serviceObject.Properties.BindPropertiesToResultTable();
                            }
                        }
                        else
                        {
                            // Call the OpenAI API with attachments (if any).
                            var responseJson = CallOpenAIAPI(apiKey, systemPrompt, model, userPrompt, endpointUrl, imageAttachments);

                            // Parse response to extract content.
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
                                // Could not parse top-level; just return raw.
                                serviceObject.Properties.InitResultTable();
                                SetOutputValue(outputs, "FullResponse", responseJson);
                                serviceObject.Properties.BindPropertiesToResultTable();
                                return;
                            }

                            finalContentJson = contentString;

                            // Cache the response if enabled.
                            if (useCache && !string.IsNullOrEmpty(finalContentJson))
                            {
                                CacheResponse(userPrompt, finalContentJson, cacheDuration);
                            }

                            if (returnAsList)
                            {
                                // Handle as list.
                                HandleListResponse(finalContentJson, returnPropsList, outputs, serviceObject);
                            }
                            else
                            {
                                // Single row.
                                serviceObject.Properties.InitResultTable();
                                SetOutputValue(outputs, "FullResponse", finalContentJson);
                                try
                                {
                                    JObject innerJson = JObject.Parse(finalContentJson);
                                    foreach (var propName in returnPropsList)
                                    {
                                        JToken valToken = innerJson.SelectToken(propName);
                                        string val = valToken != null ? valToken.ToString() : string.Empty;
                                        SetOutputValue(outputs, propName, val);
                                    }
                                }
                                catch (JsonReaderException)
                                {
                                    // The content wasn't valid JSON; no properties extracted.
                                }
                                serviceObject.Properties.BindPropertiesToResultTable();
                            }
                        }
                        break;
                    }
                case METHOD_INVALIDATE_CACHE:
                    {
                        // Clear entire cache for this service instance.
                        InvalidateAllCacheEntries();
                        break;
                    }
            }
        }

        private string MapInputPropertiesToJson(string inputPropsJsonStr, Property[] inputs)
        {
            var mappedProperties = String.Empty;
            var inputPropsJsonObj = JObject.Parse(inputPropsJsonStr);

            foreach (var input in inputs)
            {
                if (inputPropsJsonObj.ContainsKey(input.Name)) {
                    inputPropsJsonObj[input.Name] = input.Value?.ToString() ?? "";
                }
            }
            return inputPropsJsonObj.ToString() ?? "";
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

        public List<string> GetJsonPropertiesFromConfig(string propertiesList)
        {
            var props = new List<string>();
          
            if (!string.IsNullOrWhiteSpace(propertiesList))
            {
                props = propertiesList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
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

        private string CallOpenAIAPI(string apiKey, string systemPrompt, string model, string userPrompt, string endpointUrl, Dictionary<string, string> attachments)
        {
            // Build the content for the "user" message.
            // Start with the text portion.
            var userContent = new List<object>();
            userContent.Add(new { type = "text", text = userPrompt });

            // Add each attachment as an image_url object.
            if (attachments != null && attachments.Any())
            {
                foreach (var att in attachments.Values)
                {
                    // Construct a data URI assuming PNG format; adjust if needed.
                    string dataUri = $"data:image/png;base64,{att}";
                    userContent.Add(new { type = "image_url", image_url = new { url = dataUri } });
                }
            }

            var requestBody = new
            {
                model = model,
                messages = new object[]
                {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userContent }
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
            public string Id { get; set; } // key: hashed userPrompt
            public string Response { get; set; }
            public DateTime ExpiresAt { get; set; }
        }

        private string GetHashedKey(string input)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hash = sha256.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private string GetDbFilePath()
        {
            // Unique DB file per service instance
            string serviceInstanceName = this.Service.Name;
            return $"OpenAICache_{serviceInstanceName}.db";
        }

        private string GetCachedResponse(string userPrompt)
        {
            string hashedKey = GetHashedKey(userPrompt);
            using (var db = new LiteDatabase($"filename={GetDbFilePath()};mode=Shared"))
            {
                var col = db.GetCollection<CacheEntry>("cache");
                var entry = col.FindById(hashedKey);
                if (entry != null && entry.ExpiresAt > DateTime.UtcNow)
                {
                    return entry.Response;
                }
                else if (entry != null && entry.ExpiresAt <= DateTime.UtcNow)
                {
                    col.Delete(hashedKey);
                }
            }
            return null;
        }

        private void CacheResponse(string userPrompt, string response, TimeSpan duration)
        {
            string hashedKey = GetHashedKey(userPrompt);
            using (var db = new LiteDatabase($"filename={GetDbFilePath()};mode=Shared"))
            {
                var col = db.GetCollection<CacheEntry>("cache");
                var entry = new CacheEntry
                {
                    Id = hashedKey,
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

        private string ParseCSVToJson(string csv)
        {
            var json = new StringBuilder();
            json.Append("{ ");

            // Split the CSV string on commas and trim any whitespace.
            var tokens = csv.Split(',');
            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i].Trim();
                json.Append($"\"{token}\": \"string\"");
                if (i < tokens.Length - 1)
                {
                    json.Append(", ");
                }
            }

            json.Append(" }");
            return json.ToString();
        }


        public override void Extend()
        {
            throw new NotImplementedException();
        }
    }
}
