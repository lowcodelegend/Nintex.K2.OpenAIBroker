This service broker calls the OpenAI completions API endpoint.  You can select which model but it defaults to gpt-4-turbo.
You can set a System Promt at the Service Instance level - intention is that system prompt forces the llm to only output json.
You can provide a comma separated list of properties to extract from the json
When called, the smartobject will return a "FullResponse" and if possible parse the response for the listed properties.

Build in VS .net Framework 4.8, or just take the DLLs from the Debug folder
Deploy to  K2/ServiceBroker folder
Register Service Type
Register a service instnace per needed OpenAI query
