# serilog-redaction

A sample project demonstrating a more complete approach to Serilog text redaction.

This improves on widely-available implementations of redaction with Serilog by processing message templates,
exceptions (messages, types, stack traces), and property names &mdash; all of which are frequently overlooked.

See `Program.cs` for the complete example.

## Security

This example **does not consider redaction to be a security boundary**: applying redaction does not make the
resulting log output safe enough for public/untrusted display.

Redaction can improve the security of systems, and serve as an additional safeguard for secrets or PII, but it
is not sufficient as a primary protection. As an example, text encodings, Unicode handling, and culture-specific
formatting quirks provide broad vectors for subverting redaction attempts.

However, if you're able to find a way to exfiltrate redacted information that the example doesn't account for,
please raise an issue!

> As the code serves as an example only, it doesn't include tests.

## Performance

Redaction has a performance cost in scanning for redaction targets, and allocating replacements for redacted event
components. For many use cases this will be inconsequential, but if performance is critical, consider implementing
a mechanism for reviewing and marking high-frequency events as "safe", to bypass redaction.

> As this code is an example, only the simplest, most obvious performance optimizations are made. There are many
> low-hanging fruit remaining.

## Data validity

Redacting property names in events and structured data means that the output may no longer follow an expected format
or schema. This is considered of secondary importance compared with the need to limit propagation of redacted info, but
it's worth keeping in mind.
