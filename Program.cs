using System.Text.RegularExpressions;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Redacted(
        ["(?i)apple", "t.st"],
        wt => wt.Console(new CompactJsonFormatter()))
    .CreateLogger();

try
{
    Log.Information("Tests are testing");
    Log.Information("Another {Fruit}", "apple");
    Log.ForContext("Apple", "Granny Smith").Information("Third");
    Log.Information("{@Request}", new { Url = "apple.com" });
    throw new Exception("And another apple here.");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
}

Log.CloseAndFlush();

static class LoggerSinkConfigurationRedactionExtensions
{
    public static LoggerConfiguration Redacted(
        this LoggerSinkConfiguration loggerSinkConfiguration,
        IEnumerable<string> targets,
        Action<LoggerSinkConfiguration> configureSink,
        string replacementText = "REDACTED")
    {
        var targetRegexes = targets.Select(t => new Regex(t, RegexOptions.Compiled));
        return LoggerSinkConfiguration.Wrap(
            loggerSinkConfiguration,
            sink => new RedactingSink(sink, targetRegexes, replacementText),
            configureSink);
    }
}

class RedactedException : Exception
{
    readonly string _toString;

    public RedactedException(string toString)
     : base(message: "This exception has been redacted; only `ToString()` will provide detailed information.")
    {
        _toString = toString;
    }

    public override string? StackTrace => null;

    public override string? Source => null;

    public override string ToString() => _toString;
}

class RedactionCount
{
    ulong _redactionCount;

    public ulong Value => _redactionCount;

    public void Increment()
    {
        _redactionCount += 1;
    }
}

class RedactingSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    readonly ILogEventSink _inner;
    readonly string _contentReplacement;
    readonly Regex[] _targets;

    ulong _nextRedaction;

    public RedactingSink(ILogEventSink inner, IEnumerable<Regex> targets, string contentReplacement)
    {
        _inner = inner;
        _contentReplacement = contentReplacement;
        _targets = targets.ToArray();
    }
    
    public void Emit(LogEvent logEvent)
    {
        var redactionCount = new RedactionCount();
        var exception = RedactException(logEvent.Exception, redactionCount);
        var messageTemplate = RedactMessageTemplate(logEvent.MessageTemplate, redactionCount);
        var properties = RedactProperties(logEvent.Properties, redactionCount);

        if (redactionCount.Value == 0)
        {
            _inner.Emit(logEvent);
            return;
        }

        properties.Add(new LogEventProperty("RedactionCount", new ScalarValue(redactionCount.Value)));

        var redacted = new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception,
            messageTemplate,
            properties,
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default);
        
        _inner.Emit(redacted);
    }

    List<LogEventProperty> RedactProperties(IReadOnlyDictionary<string,LogEventPropertyValue> properties, RedactionCount redactionCount)
    {
        return properties.Select(kv => new LogEventProperty(
            RedactName(kv.Key, redactionCount),
            RedactValue(kv.Value, redactionCount))).ToList();
    }

    MessageTemplate RedactMessageTemplate(MessageTemplate messageTemplate, RedactionCount redactionCount)
    {
        var initial = redactionCount.Value;
        var redacted = Redact(messageTemplate.Text, redactionCount);
        return initial == redactionCount.Value
            ? messageTemplate
            : new MessageTemplate(
                redacted.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal),
                [new TextToken(redacted)]);
    }

    Exception? RedactException(Exception? exception, RedactionCount redactionCount)
    {
        if (exception == null)
            return null;

        return new RedactedException(Redact(exception.ToString(), redactionCount));
    }

    LogEventPropertyValue RedactValue(LogEventPropertyValue value, RedactionCount redactionCount)
    {
        return value switch
        {
            DictionaryValue dictionaryValue => RedactDictionary(dictionaryValue, redactionCount),
            ScalarValue scalarValue => RedactScalar(scalarValue, redactionCount),
            SequenceValue sequenceValue => RedactSequence(sequenceValue, redactionCount),
            StructureValue structureValue => RedactStructure(structureValue, redactionCount),
            _ => RedactValue(new ScalarValue(value.ToString()), redactionCount)
        };
    }

    StructureValue RedactStructure(StructureValue structureValue, RedactionCount redactionCount)
    {
        return new StructureValue(
            structureValue.Properties.Select(p =>
                new LogEventProperty(RedactName(p.Name, redactionCount), RedactValue(p.Value, redactionCount))),
            structureValue.TypeTag != null ? Redact(structureValue.TypeTag, redactionCount) : null);
    }

    SequenceValue RedactSequence(SequenceValue sequenceValue, RedactionCount redactionCount)
    {
        return new SequenceValue(sequenceValue.Elements.Select(e => RedactValue(e, redactionCount)));
    }

    DictionaryValue RedactDictionary(DictionaryValue dictionaryValue, RedactionCount redactionCount)
    {
        return new DictionaryValue(
            dictionaryValue.Elements.Select(e =>
                KeyValuePair.Create(RedactScalar(e.Key, redactionCount), RedactValue(e.Value, redactionCount))));
    }

    ScalarValue RedactScalar(ScalarValue scalarValue, RedactionCount redactionCount)
    {
        if (scalarValue is { Value: string s })
        {
            // Likely a lot of these, so avoid the extra alloc.
            if (RequiresRedaction(s))
            {
                return new ScalarValue(Redact(s, redactionCount));
            }

            return scalarValue;
        }

        var asString = scalarValue.ToString();
        if (RequiresRedaction(asString))
        {
            return new ScalarValue(Redact(asString, redactionCount));
        }

        return scalarValue;
    }

    string RedactName(string name, RedactionCount redactionCount)
    {
        // Differs from `Redact(string)` because any hit will trigger complete replacement of the
        // name, avoiding data loss at later stages due to unexpected name content.
        if (RequiresRedaction(name))
        {
            redactionCount.Increment();
            return $"Redacted{Interlocked.Increment(ref _nextRedaction)}";
        }

        return name;
    }
    
    bool RequiresRedaction(string s)
    {
        foreach (var regex in _targets)
        {
            if (regex.Match(s).Success)
            {
                return true;
            }
        }

        return false;
    }

    string Redact(string s, RedactionCount redactionCount)
    {
        var redacted = s;
        foreach (var regex in _targets)
        {
            redacted = regex.Replace(redacted, _ =>
            {
                redactionCount.Increment();
                return _contentReplacement;
            });
        }
        return redacted;
    }

    public void Dispose()
    {
        (_inner as IDisposable)?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_inner is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }
}
