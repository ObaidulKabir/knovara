using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Knovara.Contracts
{
    public record AskRequest([property: JsonPropertyName("question")] string Question);
    public record AskResponse([property: JsonPropertyName("answer")] string Answer);
}
