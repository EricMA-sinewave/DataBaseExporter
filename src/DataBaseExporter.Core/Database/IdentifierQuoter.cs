using DataBaseExporter.Core.Configuration;

namespace DataBaseExporter.Core.Database;

public sealed class IdentifierQuoter
{
    private readonly string _openingQuote;
    private readonly string _closingQuote;

    public IdentifierQuoter(DatabaseConnectionOptions options)
    {
        _openingQuote = options.OpeningIdentifierQuote;
        _closingQuote = options.ClosingIdentifierQuote;
    }

    public string QuoteTable(DatabaseTable table)
    {
        return string.IsNullOrWhiteSpace(table.Schema)
            ? QuotePart(table.Name)
            : $"{QuotePart(table.Schema)}.{QuotePart(table.Name)}";
    }

    private string QuotePart(string part)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            throw new ArgumentException("Identifier part cannot be empty.", nameof(part));
        }

        return _openingQuote + part.Replace(_closingQuote, _closingQuote + _closingQuote, StringComparison.Ordinal) + _closingQuote;
    }
}
