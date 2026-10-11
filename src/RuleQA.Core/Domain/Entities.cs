namespace RuleQA.Core.Domain;

public enum DocumentKind
{
    /// <summary>A regulation: the basis for answers.</summary>
    Regulation,

    /// <summary>FAQ, guides and other material that the regulations take precedence over.</summary>
    Reference,
}

/// <summary>One imported file (a regulation or reference material).</summary>
public class RuleDocument
{
    public int Id { get; set; }

    /// <summary>Path relative to the rules folder, with "/" separators.</summary>
    public string Path { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Title without version notes such as "（2021年版）"; versions of one regulation share it.</summary>
    public string BaseTitle { get; set; } = "";

    public DocumentKind Kind { get; set; }

    /// <summary>The latest enforcement or revision date written in the document.</summary>
    public DateOnly? EffectiveDate { get; set; }

    /// <summary>The document says it is abolished or an old version.</summary>
    public bool IsAbolished { get; set; }

    /// <summary>A newer version exists or the document is abolished; not searched by default.</summary>
    public bool IsSuperseded { get; set; }

    /// <summary>Path of the version that replaced this one.</summary>
    public string? SupersededBy { get; set; }

    public string Encoding { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime ImportedAtUtc { get; set; }

    /// <summary>Parse warnings, one per line.</summary>
    public string Warnings { get; set; } = "";

    public List<Article> Articles { get; set; } = [];

    /// <summary>Path without the extension; the prefix of every article key.</summary>
    public string Key => System.IO.Path.ChangeExtension(Path, null);
}

/// <summary>A searchable unit: an article, a paragraph (項) of a long article, a section or a paragraph.</summary>
public class Article
{
    public int Id { get; set; }
    public int RuleDocumentId { get; set; }
    public RuleDocument Document { get; set; } = null!;

    /// <summary>Stable across re-imports: "{document key}#{label}", for example "03_旅費規程#第5条".</summary>
    public string Key { get; set; } = "";

    public int Order { get; set; }
    public string? Chapter { get; set; }

    /// <summary>"第5条", "第6条の2", "第7条第3項", "附則", a section heading or "段落2".</summary>
    public string Label { get; set; } = "";

    public string? Heading { get; set; }
    public string Body { get; set; } = "";

    public byte[]? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }

    /// <summary>"旅費規程 第5条（国内出張の日当）".</summary>
    public string DisplayName => $"{Document.Title} {Label}" + (Heading is null ? "" : $"（{Heading}）");

    /// <summary>The text that is embedded; a change means the vector must be rebuilt.</summary>
    public string EmbeddingText => $"{Document.Title} {Chapter} {Label} {Heading}\n{Body}";
}

public enum AnswerStatus
{
    /// <summary>The AI answered from the cited articles.</summary>
    Answered,

    /// <summary>The rules do not cover the question (the AI said so, or cited nothing valid).</summary>
    NotInRules,

    /// <summary>No AI is set or it failed; the candidates are shown as they are.</summary>
    SearchOnly,

    /// <summary>The AI judged that the question has nothing to do with the company or work (weather, news, small talk ...).</summary>
    OffTopic,
}

/// <summary>One question and the answer shown for it.</summary>
public class QaRecord
{
    public int Id { get; set; }

    /// <summary>Questions asked in one conversation share this ID.</summary>
    public string ConversationId { get; set; } = "";

    /// <summary>1 for the first question of the conversation.</summary>
    public int Turn { get; set; }

    public DateTime AskedAtUtc { get; set; }
    public string Question { get; set; } = "";
    public AnswerStatus Status { get; set; }
    public string AnswerText { get; set; } = "";

    /// <summary>Article keys, one per line. Keys (not IDs) so the history survives re-imports.</summary>
    public string CitationKeys { get; set; } = "";

    /// <summary>Keys of the articles found by search, one per line.</summary>
    public string CandidateKeys { get; set; } = "";

    public string? AiName { get; set; }
    public string? Error { get; set; }
    public int ElapsedMs { get; set; }
}
