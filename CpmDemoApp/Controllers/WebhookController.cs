using System.Text;
using Microsoft.AspNetCore.Mvc;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using System.Text.Json;
using CpmDemoApp.Models;
using Azure.AI.OpenAI;
using Azure.Communication.Messages;
using Microsoft.Extensions.Options;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CpmDemoApp.Controllers
{
    [Route("webhook")]
    public class WebhookController : Controller
    {
        private static bool _clientsInitialized;
        private static NotificationMessagesClient _notificationMessagesClient;
        private static Guid _channelRegistrationId;
        private static AzureOpenAIClient _azureOpenAIClient;
        private static string _deploymentName;
        private static SearchClient _searchClient;

        private static string SystemPrompt =>
            "You are a helpful wedding assistant. Your task is to provide accurate information about the wedding based on the search results provided to you. " +
            "Always prioritize information from the search results when answering questions. " +
            "If the search results don't contain the answer, politely say you don't have that specific information and offer to help with something else. " +
            "Keep your responses friendly, concise, and accurate. " +
            "Don't make up information that's not in the search results.";

        private bool EventTypeSubcriptionValidation
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "SubscriptionValidation";

        private bool EventTypeNotification
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "Notification";

        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public WebhookController(
            IOptions<NotificationMessagesClientOptions> notificationOptions,
            IOptions<OpenAIClientOptions> AIOptions,
            IOptions<AzureAISearchOptions> searchOptions)
        {
            if (!_clientsInitialized)
            {
                _channelRegistrationId = Guid.Parse(notificationOptions.Value.ChannelRegistrationId);
                _deploymentName = AIOptions.Value.DeploymentName;
                _notificationMessagesClient = new NotificationMessagesClient(notificationOptions.Value.ConnectionString);
                _azureOpenAIClient = new AzureOpenAIClient(new Uri(AIOptions.Value.Endpoint), new AzureKeyCredential(AIOptions.Value.Key));
                _searchClient = new SearchClient(
                    new Uri(searchOptions.Value.Endpoint),
                    searchOptions.Value.IndexName,
                    new AzureKeyCredential(searchOptions.Value.Key));
                _clientsInitialized = true;
            }
        }

        [HttpOptions]
        public async Task<IActionResult> Options()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var webhookRequestOrigin = HttpContext.Request.Headers["WebHook-Request-Origin"].FirstOrDefault();
                var webhookRequestCallback = HttpContext.Request.Headers["WebHook-Request-Callback"];
                var webhookRequestRate = HttpContext.Request.Headers["WebHook-Request-Rate"];
                HttpContext.Response.Headers.Add("WebHook-Allowed-Rate", "*");
                HttpContext.Response.Headers.Add("WebHook-Allowed-Origin", webhookRequestOrigin);
            }

            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> Post()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var jsonContent = await reader.ReadToEndAsync();

                // Check the event type.
                // Return the validation code if it's a subscription validation request. 
                if (EventTypeSubcriptionValidation)
                {
                    return await HandleValidation(jsonContent);
                }
                else if (EventTypeNotification)
                {
                    return await HandleGridEvents(jsonContent);
                }

                return BadRequest();
            }
        }

        private async Task<JsonResult> HandleValidation(string jsonContent)
        {
            var eventGridEvent = JsonSerializer.Deserialize<EventGridEvent[]>(jsonContent, _jsonOptions).First();
            var eventData = JsonSerializer.Deserialize<SubscriptionValidationEventData>(eventGridEvent.Data.ToString(), _jsonOptions);
            var responseData = new SubscriptionValidationResponse
            {
                ValidationResponse = eventData.ValidationCode
            };
            return new JsonResult(responseData);
        }

        private async Task<IActionResult> HandleGridEvents(string jsonContent)
        {
            var eventGridEvents = JsonSerializer.Deserialize<EventGridEvent[]>(jsonContent, _jsonOptions);
            foreach (var eventGridEvent in eventGridEvents)
            {
                if (eventGridEvent.EventType.Equals("microsoft.communication.advancedmessagereceived", StringComparison.OrdinalIgnoreCase))
                {
                    var messageData = JsonSerializer.Deserialize<AdvancedMessageReceivedEventData>(eventGridEvent.Data.ToString(), _jsonOptions);
                    Messages.MessagesListStatic.Add(new Message
                    {
                        Text = $"Customer({messageData.From}): \"{messageData.Content}\""
                    });
                    Messages.ConversationHistory.Add(new UserMessage { Content = messageData.Content });
                    await RespondToCustomerAsync(messageData.From);
                }
            }

            return Ok();
        }

        private async Task RespondToCustomerAsync(string numberToRespondTo)
        {
            try
            {
                var assistantResponseText = await GenerateAIResponseAsync();
                if (string.IsNullOrWhiteSpace(assistantResponseText))
                {
                    Messages.MessagesListStatic.Add(new Message
                    {
                        Text = "Error: No response generated from Azure OpenAI."
                    });
                    return;
                }

                await SendWhatsAppMessageAsync(numberToRespondTo, assistantResponseText);
                Messages.ConversationHistory.Add(new AssistantMessage { Content = assistantResponseText });
                Messages.MessagesListStatic.Add(new Message
                {
                    Text = $"Assistant: {assistantResponseText}"
                });
            }
            catch (RequestFailedException e)
            {
                Messages.MessagesListStatic.Add(new Message
                {
                    Text = $"Error: Failed to respond to \"{numberToRespondTo}\". Exception: {e.Message}"
                });
            }
        }

        private async Task<string?> GenerateAIResponseAsync()
        {
            try
            {
                // Get last user message from your custom history model
                var lastUserMessage = Messages.ConversationHistory
                    .LastOrDefault(m => m is UserMessage) as UserMessage;

                if (lastUserMessage == null)
                {
                    Console.WriteLine("Error: Could not find the last user message in history.");
                    return "I couldn't retrieve your last message. Could you please repeat it?";
                }

                // Use the .Content property from your custom UserMessage model
                string userQuery = lastUserMessage.Content;
                if (string.IsNullOrWhiteSpace(userQuery))
                {
                    Console.WriteLine("Error: Last user message content is empty.");
                    return "Your last message was empty. Could you please ask again?";
                }

                // Perform the search
                string searchResults = await SearchIndexAsync(userQuery);
                Console.WriteLine($"Search results for query '{userQuery}':\n{searchResults}"); // For debugging

                // --- Correct usage of Azure.AI.OpenAI types ---

                // 1. Create ChatCompletionsOptions
                var completionOptions = new ChatCompletionsOptions()
                {
                    // DeploymentName is passed to GetChatCompletionsAsync, not set here
                    MaxTokens = 800,
                    Temperature = 0.7f
                    // Add other options if needed (e.g., StopSequences)
                };

                // 2. Add messages to the options' Messages list
                // System Prompt + Search Results
                completionOptions.Messages.Add(new ChatRequestSystemMessage(SystemPrompt + "\n\nRelevant Information Found:\n" + searchResults));

                // Add conversation history (using .Content from your custom models)
                foreach (var message in Messages.ConversationHistory.TakeLast(10)) // Use your custom history
                {
                    if (message is UserMessage userMsg)
                    {
                        completionOptions.Messages.Add(new ChatRequestUserMessage(userMsg.Content)); // Use .Content
                    }
                    else if (message is AssistantMessage assistantMsg)
                    {
                        completionOptions.Messages.Add(new ChatRequestAssistantMessage(assistantMsg.Content)); // Use .Content
                    }
                }

                // 3. Call GetChatCompletionsAsync on the AzureOpenAIClient instance
                Response<ChatCompletions> response = await _azureOpenAIClient.GetChatCompletionsAsync(
                    deploymentOrModelName: _deploymentName, // Pass deployment name here
                    completionOptions);                     // Pass the configured options

                // 4. Extract the response content
                // Check if there are choices and a message before accessing
                ChatResponseMessage responseMessage = response.Value?.Choices?.FirstOrDefault()?.Message;
                string responseText = responseMessage?.Content;

                if (string.IsNullOrWhiteSpace(responseText))
                {
                     Console.WriteLine("Warning: Azure OpenAI returned an empty response.");
                     return "I received an empty response. Could you try asking differently?";
                }

                return responseText;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating AI response: {ex}"); // Log the full exception
                // Consider logging ex.ToString() for stack trace in real scenarios
                return "I'm having trouble generating a response right now. Please try again in a moment.";
            }
        }

        private async Task<string> SearchIndexAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "Please provide a question or topic to search for.";
            }

            SearchResults<SearchDocument> response = null;
            SearchOptions options = null;
            string searchTypeAttempted = "None";

            try
            {
                // --- Attempt 1: Semantic Search with Vector (Best Quality - SDK 11.5+) ---
                searchTypeAttempted = "Semantic + Vector";
                Console.WriteLine($"Attempting {searchTypeAttempted} Search for query: '{query}'");
                options = new SearchOptions
                {
                    Size = 3,
                    Select = { "chunk", "title" },
                    QueryType = SearchQueryType.Semantic,
                    SemanticSearch = new SemanticSearchOptions
                    {
                        SemanticConfigurationName = "vector-1745864508214-semantic-configuration"
                    },
                    VectorSearch = new VectorSearchOptions
                    {
                        Queries = { new VectorizableTextQuery(query) { KNearestNeighborsCount = 3, Fields = { "text_vector" } } },
                        VectorizerOptions = new VectorizerOptions
                        {
                            VectorizerName = "vector-1745864508214-azureOpenAi-text-vectorizer"
                        }
                    }
                };
                response = await _searchClient.SearchAsync<SearchDocument>(query, options);
                Console.WriteLine($"{searchTypeAttempted} Search successful.");
            }
            catch (RequestFailedException rfEx) when (rfEx.Status == 400 && (rfEx.Message.Contains("semantic configuration") || rfEx.Message.Contains("semantic ranker") || rfEx.Message.Contains("vectorizer")))
            {
                Console.WriteLine($"{searchTypeAttempted} search failed (likely tier/config/SDK version issue): {rfEx.Message}. Falling back...");

                // --- Attempt 2: Simple Vector Search (SDK 11.5+) ---
                searchTypeAttempted = "Simple Vector";
                Console.WriteLine($"Attempting {searchTypeAttempted} Search for query: '{query}'");
                try
                {
                    options = new SearchOptions
                    {
                        Size = 3,
                        Select = { "chunk", "title" },
                        VectorSearch = new VectorSearchOptions
                        {
                            Queries = { new VectorizableTextQuery(query) { KNearestNeighborsCount = 3, Fields = { "text_vector" } } },
                            VectorizerOptions = new VectorizerOptions
                            {
                                VectorizerName = "vector-1745864508214-azureOpenAi-text-vectorizer"
                            }
                        }
                    };
                    response = await _searchClient.SearchAsync<SearchDocument>(null, options);
                    Console.WriteLine($"{searchTypeAttempted} Search successful.");
                }
                catch (Exception vectorEx)
                {
                    Console.WriteLine($"{searchTypeAttempted} search also failed: {vectorEx.Message}. Falling back to simple text search.");
                    searchTypeAttempted = "Simple Text";
                     // --- Attempt 3: Simple Text Search ---
                     Console.WriteLine($"Attempting {searchTypeAttempted} Search for query: '{query}'");
                    options = new SearchOptions { Size = 3, Select = { "chunk", "title" } };
                    response = await _searchClient.SearchAsync<SearchDocument>(query, options);
                     Console.WriteLine($"{searchTypeAttempted} Search successful.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Initial {searchTypeAttempted} attempt failed unexpectedly: {ex.Message}. Falling back to simple text search.");
                searchTypeAttempted = "Simple Text";
                // --- Attempt 3: Simple Text Search ---
                 Console.WriteLine($"Attempting {searchTypeAttempted} Search for query: '{query}'");
                options = new SearchOptions { Size = 3, Select = { "chunk", "title" } };
                response = await _searchClient.SearchAsync<SearchDocument>(query, options);
                 Console.WriteLine($"{searchTypeAttempted} Search successful.");
            }

            // --- Process Results (Remains the same) ---
            StringBuilder searchResultsBuilder = new StringBuilder();
            bool foundResults = false;

            if (response != null)
            {
                await foreach (SearchResult<SearchDocument> result in response.GetResultsAsync())
                {
                    foundResults = true;
                    string title = result.Document.TryGetValue("title", out object titleObj) && titleObj != null ? titleObj.ToString() : "N/A";
                    string chunk = result.Document.TryGetValue("chunk", out object chunkObj) && chunkObj != null ? chunkObj.ToString() : "N/A";

                    searchResultsBuilder.AppendLine($"Title: {title}");
                    searchResultsBuilder.AppendLine($"Content: {chunk}");
                    searchResultsBuilder.AppendLine("---");
                }
            }

            if (!foundResults)
            {
                string optionsJson = options != null ? JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }) : "N/A";
                Console.WriteLine($"No results found for query: '{query}' using {searchTypeAttempted} search with options: {optionsJson}");
                return "No relevant information found about this topic in the knowledge base.";
            }

            string resultsText = searchResultsBuilder.ToString();
            Console.WriteLine($"Search Results Found using {searchTypeAttempted}:\n{resultsText}");
            return resultsText;
        }

        private async Task SendWhatsAppMessageAsync(string numberToRespondTo, string message)
        {
            var recipientList = new List<string> { numberToRespondTo };
            var textContent = new TextNotificationContent(_channelRegistrationId, recipientList, message);
            await _notificationMessagesClient.SendAsync(textContent);
        }
    }
}