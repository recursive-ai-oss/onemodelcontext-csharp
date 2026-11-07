using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Binding;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// Registers tools, resources and prompts from delegates, by reflection. It is the quickest way to try something and the way to add
/// handlers that are only known at run time. It does not work in trimmed or NativeAOT apps: declare <see cref="ToolAttribute"/> methods
/// instead, and the source generator does the same work at build time. The schemas follow the same signature table the generator does.
/// </summary>
public static class ContextServerReflectionExtensions
{
  private const string Message = "Reads the delegate's signature and its parameter and result types by reflection. Use [Tool] methods with the source generator for trimmed or NativeAOT apps.";

  /// <summary>Registers a tool from a delegate.</summary>
  /// <param name="server">The server.</param>
  /// <param name="name">The tool name (1 to 128 letters, digits, <c>_</c>, <c>-</c> or <c>.</c>).</param>
  /// <param name="handler">
  /// The tool. Its parameters become the arguments (required unless they have a default value); <see cref="RequestContext"/> and
  /// <see cref="System.Threading.CancellationToken"/> parameters are filled in. It may return a string, content blocks, a
  /// <see cref="CallToolResult"/>, JSON or any serializable value, directly or in a <see cref="Task"/> or <see cref="ValueTask"/>.
  /// </param>
  /// <param name="description">What the tool does, written for a model; defaults to the delegate's <see cref="DescriptionAttribute"/>.</param>
  /// <returns>The server, for chaining.</returns>
  /// <exception cref="ArgumentException">The name is not valid.</exception>
  /// <exception cref="NotSupportedException">A parameter type has no schema in the signature table.</exception>
  [RequiresUnreferencedCode(Message)]
  [RequiresDynamicCode(Message)]
  public static ContextServer Tool(this ContextServer server, string name, Delegate handler, string? description = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(handler);
    ValidateName(name);
    var method = handler.Method;
    var parameters = method.GetParameters();
    var schema = ReflectionSchema.ForParameters(parameters);
    var known = new HashSet<string>(StringComparer.Ordinal);
    foreach (var parameter in parameters)
    {
      if (!ReflectionSchema.IsInjected(parameter.ParameterType))
      {
        known.Add(parameter.Name!);
      }
    }

    var returnType = UnwrapTask(method.ReturnType);
    var structured = returnType != typeof(void) && ReflectionSchema.IsObjectShaped(returnType) && !typeof(ContentBlock).IsAssignableFrom(returnType)
      && returnType != typeof(CallToolResult) && !ReflectionSchema.IsContentSequence(returnType);
    var definition = new ToolDefinition(name, description ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description, ToolResults.Schema(schema.ToJson()))
    {
      OutputSchema = structured ? ToolResults.Schema(ReflectionSchema.ForType(returnType, false, []).ToJson()) : default,
    };

    server.Tools.Add(definition, async (arguments, context) =>
    {
      var values = BindArguments(arguments, parameters, known, context);
      var result = await InvokeAsync(handler, values).ConfigureAwait(false);
      return ToResult(result, structured);
    });
    return server;
  }

  /// <summary>Registers a resource or resource template from a delegate.</summary>
  /// <param name="server">The server.</param>
  /// <param name="uri">The URI, or a URI template whose variables are passed to the parameters of the same name.</param>
  /// <param name="name">The programmatic name.</param>
  /// <param name="handler">Returns a string (text), a byte array (blob) or <see cref="ReadResourceResult"/>. String-typed parameters take template variables.</param>
  /// <param name="mimeType">The media type; defaults to <c>text/plain</c> for text and <c>application/octet-stream</c> for bytes.</param>
  /// <param name="description">What it is.</param>
  /// <returns>The server, for chaining.</returns>
  [RequiresUnreferencedCode(Message)]
  [RequiresDynamicCode(Message)]
  public static ContextServer Resource(this ContextServer server, string uri, string name, Delegate handler, string? mimeType = null, string? description = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentException.ThrowIfNullOrEmpty(uri);
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(handler);
    var parameters = handler.Method.GetParameters();

    ResourceHandler run = async (readUri, context) =>
    {
      var variables = uri.Contains('{', StringComparison.Ordinal) ? StringArguments.Match(uri, readUri) : new Dictionary<string, string>();
      var values = new object?[parameters.Length];
      for (var i = 0; i < parameters.Length; i++)
      {
        var type = parameters[i].ParameterType;
        values[i] = type == typeof(RequestContext) ? context
          : type == typeof(System.Threading.CancellationToken) ? context.CancellationToken
          : variables.TryGetValue(parameters[i].Name!, out var text) ? ParseVariable(text, type, parameters[i].Name!)
          : parameters[i].HasDefaultValue ? parameters[i].DefaultValue : throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Missing argument '" + parameters[i].Name + "'.");
      }

      var result = await InvokeAsync(handler, values).ConfigureAwait(false);
      return result switch
      {
        ReadResourceResult r => r,
        byte[] bytes => StringArguments.BlobResource(readUri, mimeType, bytes),
        string text => StringArguments.TextResource(readUri, mimeType, text),
        IEnumerable<ResourceContents> contents => StringArguments.Resource(contents),
        _ => StringArguments.TextResource(readUri, mimeType, result?.ToString() ?? string.Empty),
      };
    };

    if (uri.Contains('{', StringComparison.Ordinal))
    {
      server.ResourceTemplates.Add(new ResourceTemplateDefinition(uri, name) { MimeType = mimeType, Description = description }, run);
    }
    else
    {
      server.Resources.Add(new ResourceDefinition(uri, name) { MimeType = mimeType, Description = description }, run);
    }

    return server;
  }

  /// <summary>Registers a prompt from a delegate.</summary>
  /// <param name="server">The server.</param>
  /// <param name="name">The prompt name.</param>
  /// <param name="handler">
  /// Returns a string (one user message), <see cref="PromptMessage"/>s or a <see cref="GetPromptResult"/>. String-typed parameters are the prompt's
  /// arguments, required unless they have a default value.
  /// </param>
  /// <param name="description">What the prompt is for.</param>
  /// <returns>The server, for chaining.</returns>
  [RequiresUnreferencedCode(Message)]
  [RequiresDynamicCode(Message)]
  public static ContextServer Prompt(this ContextServer server, string name, Delegate handler, string? description = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(handler);
    var parameters = handler.Method.GetParameters();
    var arguments = new List<PromptArgument>();
    foreach (var parameter in parameters)
    {
      if (!ReflectionSchema.IsInjected(parameter.ParameterType))
      {
        arguments.Add(new PromptArgument
        {
          Name = parameter.Name!,
          Description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description,
          Required = parameter.HasDefaultValue ? null : true,
        });
      }
    }

    server.Prompts.Add(new PromptDefinition(name, description) { Arguments = arguments }, async (values, context) =>
    {
      var bound = new object?[parameters.Length];
      for (var i = 0; i < parameters.Length; i++)
      {
        var type = parameters[i].ParameterType;
        bound[i] = type == typeof(RequestContext) ? context
          : type == typeof(System.Threading.CancellationToken) ? context.CancellationToken
          : values.TryGetValue(parameters[i].Name!, out var text) ? ParseVariable(text, type, parameters[i].Name!)
          : parameters[i].HasDefaultValue ? parameters[i].DefaultValue : throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Missing argument '" + parameters[i].Name + "'.");
      }

      var result = await InvokeAsync(handler, bound).ConfigureAwait(false);
      return result switch
      {
        GetPromptResult r => r,
        string text => StringArguments.UserPrompt(description, text),
        IEnumerable<PromptMessage> messages => StringArguments.Prompt(description, messages),
        _ => StringArguments.UserPrompt(description, result?.ToString() ?? string.Empty),
      };
    });
    return server;
  }

  private static void ValidateName(string name)
  {
    ArgumentException.ThrowIfNullOrEmpty(name);
    if (name.Length > 128)
    {
      throw new ArgumentException("A tool name has at most 128 characters.", nameof(name));
    }

    foreach (var c in name)
    {
      if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
      {
        throw new ArgumentException("A tool name holds only ASCII letters, digits, '_', '-' and '.'.", nameof(name));
      }
    }
  }

  private static Type UnwrapTask(Type type)
  {
    if (type == typeof(Task) || type == typeof(ValueTask))
    {
      return typeof(void);
    }

    if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
    {
      return type.GetGenericArguments()[0];
    }

    return type;
  }

  [RequiresUnreferencedCode(Message)]
  [RequiresDynamicCode(Message)]
  private static object?[] BindArguments(JsonElement arguments, ParameterInfo[] parameters, HashSet<string> known, RequestContext context)
  {
    if (arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
    {
      throw new ToolException("The arguments must be a JSON object.");
    }

    if (arguments.ValueKind == JsonValueKind.Object)
    {
      foreach (var property in arguments.EnumerateObject())
      {
        if (!known.Contains(property.Name))
        {
          throw new ToolException("Unknown argument '" + (property.Name.Length <= 64 ? property.Name : property.Name[..64] + "...") + "'.");
        }
      }
    }

    var nullability = new NullabilityInfoContext();
    var values = new object?[parameters.Length];
    for (var i = 0; i < parameters.Length; i++)
    {
      var parameter = parameters[i];
      if (parameter.ParameterType == typeof(RequestContext))
      {
        values[i] = context;
        continue;
      }

      if (parameter.ParameterType == typeof(System.Threading.CancellationToken))
      {
        values[i] = context.CancellationToken;
        continue;
      }

      JsonElement element = default;
      if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(parameter.Name!, out element))
      {
        values[i] = parameter.HasDefaultValue ? parameter.DefaultValue : throw new ToolException("Argument '" + parameter.Name + "' is required.");
        continue;
      }

      var nullable = Nullable.GetUnderlyingType(parameter.ParameterType) is not null
        || (!parameter.ParameterType.IsValueType && nullability.Create(parameter).ReadState == NullabilityState.Nullable);
      if (element.ValueKind == JsonValueKind.Null)
      {
        values[i] = nullable ? null : throw new ToolException("Argument '" + parameter.Name + "' must not be null.");
        continue;
      }

      try
      {
        values[i] = element.Deserialize(parameter.ParameterType, ReflectionSchema.Options);
      }
      catch (JsonException)
      {
        throw new ToolException("Argument '" + parameter.Name + "' is not valid.");
      }
    }

    return values;
  }

  [RequiresUnreferencedCode(Message)]
  private static async ValueTask<object?> InvokeAsync(Delegate handler, object?[] values)
  {
    object? result;
    try
    {
      result = handler.DynamicInvoke(values);
    }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    {
      System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
      throw;
    }

    switch (result)
    {
      case Task task:
        await task.ConfigureAwait(false);
        var taskType = task.GetType();
        return taskType.IsGenericType ? taskType.GetProperty("Result")!.GetValue(task) : null;
      case ValueTask valueTask:
        await valueTask.ConfigureAwait(false);
        return null;
      default:
        if (result is not null && result.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
          var asTask = (Task)type.GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(result, null)!;
          await asTask.ConfigureAwait(false);
          return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
        }

        return result;
    }
  }

  [RequiresUnreferencedCode(Message)]
  [RequiresDynamicCode(Message)]
  private static CallToolResult ToResult(object? result, bool structured)
  {
    switch (result)
    {
      case null:
        return ToolResults.Empty();
      case CallToolResult call:
        return call;
      case string text:
        return ToolResults.Text(text);
      case ContentBlock block:
        return ToolResults.Part(block);
      case IEnumerable<ContentBlock> blocks:
        return ToolResults.Parts(blocks);
      case JsonNode node:
        return ToolResults.Json(node);
      case JsonElement element:
        return ToolResults.Json(element);
      case Enum member:
        return ToolResults.EnumName(member.ToString());
    }

    var json = JsonSerializer.SerializeToElement(result, result.GetType(), ReflectionSchema.Options);
    return structured && json.ValueKind == JsonValueKind.Object
      ? new CallToolResult { Content = [TextContent.Of(json.GetRawText())], StructuredContent = json }
      : CallToolResult.FromText(json.ValueKind == JsonValueKind.String ? json.GetString()! : json.GetRawText());
  }

  [RequiresUnreferencedCode(Message)]
  private static object? ParseVariable(string text, Type type, string name)
  {
    type = Nullable.GetUnderlyingType(type) ?? type;
    try
    {
      if (type == typeof(string))
      {
        return text;
      }

      return type.IsEnum
        ? Enum.Parse(type, text, ignoreCase: false)
        : Convert.ChangeType(text, type, System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Argument '" + name + "' is not valid.");
    }
  }
}
