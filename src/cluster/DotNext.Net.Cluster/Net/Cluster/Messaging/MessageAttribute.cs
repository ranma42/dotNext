using System.Net.Mime;

namespace DotNext.Net.Cluster.Messaging;

using Runtime.Serialization;

/// <summary>
/// Indicates that the type can be used as message payload.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public abstract class MessageAttribute : Attribute
{
    private string? mimeType;

    /// <summary>
    /// Initializes a new instance of the attribute.
    /// </summary>
    /// <param name="name">The name of the message.</param>
    protected MessageAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>
    /// Gets the name of the message.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets MIME type of the message.
    /// </summary>
    public string MimeType
    {
        get => mimeType.IfNullOrEmpty(MediaTypeNames.Application.Octet);
        set => mimeType = value;
    }
}

/// <summary>
/// Indicates that the type can be used as message payload.
/// </summary>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
/// <seealso cref="MessagingClient"/>
/// <seealso cref="MessageHandler"/>
public sealed class MessageAttribute<TMessage> : MessageAttribute
    where TMessage : notnull, ISerializable<TMessage>
{
    /// <summary>
    /// Initializes a new instance of the attribute.
    /// </summary>
    /// <param name="name">The name of the message.</param>
    public MessageAttribute(string name)
        : base(name)
    {
    }
}