using System.Collections.Generic;

namespace CpmDemoApp.Models
{
    public static class Messages
    {
        public static List<Message> MessagesListStatic { get; } = new List<Message>();
        public static List<ChatMessage> ConversationHistory { get; } = new List<ChatMessage>();
    }

    public class Message
    {
        public string Text { get; set; }
    }

    public abstract class ChatMessage
    {
        public string Content { get; set; }
    }

    public class UserMessage : ChatMessage
    {
    }

    public class AssistantMessage : ChatMessage
    {
    }
}
