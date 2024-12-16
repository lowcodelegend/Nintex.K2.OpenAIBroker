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

namespace Nintex.K2
{
    public class OpenAIBroker : ServiceAssemblyBase
    {
        // Constants for Service Keys
        private const string CONFIG_OPENAI_KEY = "OpenAIKey";
        private const string CONFIG_OPENAI_SYSTEM_PROMPT = "OpenAISystemPrompt";
        private const string CONFIG_OPENAI_MODEL = "OpenAIModel";
        private const string CONFIG_JSON_PROPERTIES = "JsonPropertiesToExtract"; // Comma-separated list

        public override string GetConfigSection()
        {
            // Define the configuration properties required for the service instance.
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_KEY, true, "Your OpenAI API Key");
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_SYSTEM_PROMPT, true, "Always return the response in JSON format.");
            this.Service.ServiceConfiguration.Add(CONFIG_OPENAI_MODEL, true, "gpt-4-turbo");
            this.Service.ServiceConfiguration.Add(CONFIG_JSON_PROPERTIES, true, "Comma-separated list of JSON properties to return from inside the response.choice[0].content object e.g. id,Mesage[0].item.");
            return base.GetConfigSection();
        }

        public override string DescribeSchema()
        {
            this.Service.Name = "OpenAISvcBroker";
            this.Service.MetaData.DisplayName = "OpenAI Dynamic SmartObject Service";
          
            // Create the Service Object dynamically
            var so = new ServiceObject();
            so.Name = "OpenAIResponse";
            so.MetaData.DisplayName = "OpenAI Response Object";
            so.Active = true;

            // We'll create one method: "GetResponse" which will accept a user prompt, call OpenAI, and return specified JSON properties plus the full response.
            var getResponseMethod = new Method();
            getResponseMethod.Name = "GetResponse";
            getResponseMethod.MetaData.DisplayName = "Get OpenAI Response";
            getResponseMethod.Type = MethodType.Read;

            // Input property: User Prompt
            var userPromptProperty = new Property("UserPrompt");
            userPromptProperty.Type = "Memo";
            userPromptProperty.MetaData.DisplayName = "User Prompt";
            so.Properties.Add(userPromptProperty);

            // Add input to method
            getResponseMethod.InputProperties.Add(userPromptProperty);

            // Always return the full response
            var fullResponseProp = new Property("FullResponse");
            fullResponseProp.Type = "Memo";
            fullResponseProp.MetaData.DisplayName = "Full Response";
            so.Properties.Add(fullResponseProp);
            getResponseMethod.ReturnProperties.Add(fullResponseProp);

            // We will parse the comma-separated JSON properties from the service configuration
            var jsonProps = GetJsonPropertiesFromConfig();

            // For each requested JSON property, create a property on the service object and return it
            foreach (var propName in jsonProps)
            {
                var p = new Property(propName);
                p.Type = "Text";
                p.MetaData.DisplayName = propName;
                so.Properties.Add(p);
                getResponseMethod.ReturnProperties.Add(p);
            }

            so.Methods.Add(getResponseMethod);
            this.Service.ServiceObjects.Create(so);
            ServicePackage.IsSuccessful = true;
            return base.DescribeSchema();
        }

        public override void Execute()
        {
            // Retrieve configuration
            string apiKey = this.Service.ServiceConfiguration[CONFIG_OPENAI_KEY].ToString();
            string systemPrompt = this.Service.ServiceConfiguration[CONFIG_OPENAI_SYSTEM_PROMPT].ToString();
            string model = this.Service.ServiceConfiguration[CONFIG_OPENAI_MODEL].ToString();
            var jsonProps = GetJsonPropertiesFromConfig();
            ServiceObject serviceObject = Service.ServiceObjects[0];
            Method method = serviceObject.Methods[0];
            Property[] inputs = new Property[method.InputProperties.Count];
            Property[] outputs = new Property[method.ReturnProperties.Count];

            for (int i = 0; i < method.InputProperties.Count; i++)
            {
                inputs[i] = serviceObject.Properties[method.InputProperties[i]];
            }
            //populate the return properties collection
            for (int i = 0; i < method.ReturnProperties.Count; i++)
            {
                outputs[i] = serviceObject.Properties[method.ReturnProperties[i]];
            }

            // Retrieve user prompt from inputs
            string userPrompt = GetInputValue(inputs, "UserPrompt");

            // Call the OpenAI API
            var responseJson = CallOpenAIAPI(apiKey, systemPrompt, model, userPrompt);

            serviceObject.Properties.InitResultTable();
            // Always set the FullResponse property - first as the raw response, overriden later
            SetOutputValue(outputs, "FullResponse", responseJson);

            // Attempt to parse top-level JSON
            JObject jsonObj;
            try
            {
                jsonObj = JObject.Parse(responseJson);
            }
            catch (JsonReaderException)
            {
                // If we can't parse at all, we can't proceed further.
                // FullResponse is already set, so just return.
                return;
            }

            // Extract the content from choices[0].message.content
            JToken contentToken = jsonObj.SelectToken("choices[0].message.content");
            if (contentToken != null)
            {
                string contentString = contentToken.ToString();

                // contentString itself should be JSON; parse it
                try
                {
                    JObject innerJson = JObject.Parse(contentString);

                    //Override th previous FullResponse and scope it just to the main response content
                    SetOutputValue(outputs, "FullResponse", innerJson.ToString());

                    // Now extract the requested properties from the inner JSON
                    foreach (var propName in jsonProps)
                    {
                        JToken valToken = innerJson.SelectToken(propName);
                        string val = valToken != null ? valToken.ToString() : string.Empty;
                        SetOutputValue(outputs, propName, val);
                    }
                }
                catch (JsonReaderException)
                {
                    // The content wasn't valid JSON, so we cannot extract properties.
                    // We do nothing more, but FullResponse is still available.
                }

            }
                serviceObject.Properties.BindPropertiesToResultTable();
        }

        public List<string> GetJsonPropertiesFromConfig()
        {
            var props = new List<string>();

            string raw = this.Service.ServiceConfiguration[CONFIG_JSON_PROPERTIES].ToString();
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

        private string CallOpenAIAPI(string apiKey, string systemPrompt, string model, string userPrompt)
        {
            // Build request payload for Chat Completions
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
                var response = client.PostAsync("https://api.openai.com/v1/chat/completions", content).Result;
                response.EnsureSuccessStatusCode();

                var responseContent = response.Content.ReadAsStringAsync().Result;
                return responseContent;
            }
        }

        public override void Extend()
        {
            throw new NotImplementedException();
        }
    }
}
