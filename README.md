# Nintex K2 Service Broker for OpenAI-Compatible API

This service broker allows Nintex K2 to interact with an OpenAI-compatible completions API, enabling structured input and output parsing.  It only supports flat/single level structures of string type.

## Features
- Connects K2 workflows to an OpenAI-compatible completions API.
- Supports structured inputs and outputs parsed to SmartObject properties
- Caching options for optimized performance.
- Configurable API endpoints, models, and other parameters.
- Supports both single and list result outputs.

## Prerequisites
- Nintex K2 installed and configured.
- An OpenAI-compatible API and key.
- .NET Framework and tools for building the service broker DLL.  

---

## Installation & Deployment

### 1. Build the DLL
1. Clone or download the source code.
2. Open the solution in Visual Studio.
3. Build the project to generate the `OpenAIServiceBroker.dll`.

### 2. Deploy to K2 Server
1. Copy `OpenAIServiceBroker.dll` to the `ServiceBroker` directory on the K2 server.
2. Open the K2 Management Console.
3. Navigate to **Service Brokers** and register the new broker (May need to restart K2 Server)
4. Refresh the service types and create a new instance.

---

## Configuring the Service Instance

When adding a new service instance, the following configurations need to be provided:

| Configuration Key        | Description |
|-------------------------|-------------|
| `OpenAIEndpoint`      | The API endpoint, e.g., `https://api.openai.com/v1/chat/completions` |
| `OpenAIKey`           | API key for authentication (ensure this is securely stored) |
| `OpenAIModel`         | The model to use, e.g., `gpt-4o` |
| `OpenAISystemPrompt`  |  The system / developer level instruction for all queries made with this service instance |
| `InputPropertiesList` | Comma-separated list of expected input properties |
| `ReturnPropertiesList`  | Comma-separated list of expected output properties |
| `UseLiteDBCache`      | Whether to cache responses locally |
| `CacheDuration`       | Cache retention period (e.g., `1hr`, `1day`) |
| `ReturnAsList`        |  If `true`, results will be returned as a list.  The LLM will be properted to return a json array of the same type as the return properties list |
| `ChatTopic`          | Constrain the chat to a specific topic for safety.  E.g. Only answer questions about Leave Request Policy |

### InputPropertiesList / OutputPropertiesList

These are not JSON.  They are a CSV list of the json properties for simplicity of configuration.  If you want the LLM to take an input like:

`{
    "myPropertyInput": "value",
    "otherPropertyInput": "value"
}`

Then just provide: `myProperty,otherProperty` to the InputPropertiesList

If you want the return to be

`{
    "myPropertyOutput": "value",
    "otherPropertyOutput": "value"
}`

Then just provide `myPropertyOutput,otherPropertyOutput` to the ReturnPropertiesList

---

## Caching

The cache is quite simple is hashes the input prompt and caches it.  When a request comes in, it hashes it, checks to see if the hash exists, and if so, returns the caches one.

## Using the SmartObjects

Once the service instance is created, K2 will generate SmartObjects that interact with the API. These SmartObjects can be used in workflows to send requests and receive responses.

### **1. Sending Requests**
To send a request, use the generated SmartObject and provide the required input properties.

Service Instance Config InputPropertiesList: `requestedStartDate,requestedEndDate,currentDate,requestedLeaveType,additionalConsiderations`

#### Example Inputs:
| Property Name          | Value |
|------------------------|-------------|
| `requestStartDate`    | `2025-03-12` |
| `requestEndDate`      | `2025-03-26` |
| `currentDate`        | `2025-03-11` |
| `requestedLeaveType`  | `AnnualLeave` |
| `additionalConsiderations` | `return yes no answers as true or false` |

### **2. Receiving Responses**
The SmartObject returns structured responses based on `ReturnPropertiesList`.

Service Instance Config ReturnPropertiesList: `meetsPolicy,reason`

#### Example Outputs:
| Property Name  | Value |
|---------------|-----------------|
| `meetsPolicy` | `false` |
| `reason`      | `The request does not meet the policy due to insufficient notice period.` |

### **3. Using List Results**
If `ReturnAsList` is true, the LLM should return a JSON array with your answers and that will be parsed in to a list response.
###### Input
| `Input Property` | `Value` |
|---------------|------ |
| `City` | `Dubai` |
| `JobTitle`| `192,000 AED` |
| `Currency`|`192,000 AED` |

###### Output
| `averageSalary` | `Seniority` |
|-----------------|------------|
| `192,000 AED`  | `Junior` |
| `300,000 AED`  | `Mid-level` |
| `420,000 AED`  | `Senior` |

---
## Notes

- If you have a large prompt input, consider using an LLM to "compress" the prompt first to reduce tokens.
- Consider using a provider like Groq for ultra-fast / low-latency responses on simpler tasks.
- Consider local LLMs - known to work with LLM Studio endpoints.
- Leverage caching where possible.


