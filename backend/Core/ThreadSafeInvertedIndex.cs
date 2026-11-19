namespace Backend.Core;

public readonly struct Posting
{
    public Posting(string docId, int termFrequency)
    {
        DocId = docId;
        TermFrequency = termFrequency;
    }

    public string DocId { get; }
    public int TermFrequency { get; }
}

public sealed class DocumentInfo
{
    public DocumentInfo(string docId, string snippet)
    {
        DocId = docId;
        Snippet = snippet;
    }

    public string DocId { get; }
    public string Snippet { get; }
}

public sealed class SearchResult
{
    public SearchResult(string docId, string snippet, int score)
    {
        DocId = docId;
        Snippet = snippet;
        Score = score;
    }

    public string DocId { get; }
    public string Snippet { get; }
    public int Score { get; }
}

public sealed class ThreadSafeInvertedIndex
{
    public sealed class Entry
    {
        public Entry(string key)
        {
            Key = key;
            Postings = new Posting[4];
        }

        public string Key { get; }
        public Posting[] Postings;
        public int PostingCount;
        public Entry? Next;
    }

    public sealed class Bucket
    {
        public readonly object Lock = new object();
        internal Entry? First;
    }

    private readonly Bucket[] _buckets;
    private readonly object _documentsLock = new object();
    private DocumentInfo[] _documents;
    private int _documentCount;

    public ThreadSafeInvertedIndex(int bucketCount = 16384)
    {
        if (bucketCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(bucketCount));

        _buckets = new Bucket[bucketCount];
        for (var i = 0; i < _buckets.Length; i++)
            _buckets[i] = new Bucket();
        _documents = new DocumentInfo[16];
    }

    public int DocumentCount
    {
        get
        {
            lock (_documentsLock)
                return _documentCount;
        }
    }

    public int GetDocumentCount() => DocumentCount;
    public int Count => DocumentCount;

    public void Clear()
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            lock (_buckets[i].Lock)
                _buckets[i].First = null;
        }

        lock (_documentsLock)
        {
            Array.Clear(_documents, 0, _documents.Length);
            _documentCount = 0;
        }
    }

    public void Reset() => Clear();

    public void IndexDocument(string docId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentNullException.ThrowIfNull(text);

        var snippet = text.Length <= 200 ? text : text.Substring(0, 200);
        if (!TryAddDocument(docId, snippet))
            return;

        var start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && !char.IsLetterOrDigit(text[start]))
                start++;

            var end = start;
            while (end < text.Length && char.IsLetterOrDigit(text[end]))
                end++;

            if (end > start)
            {
                var token = Normalize(text.Substring(start, end - start));
                if (token.Length != 0)
                    AddPosting(token, docId);
            }

            start = end;
        }
    }

    public void IndexDocument(string docId, string[] tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentNullException.ThrowIfNull(tokens);

        if (!TryAddDocument(docId, string.Empty))
            return;

        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == null)
                continue;
            var normalized = Normalize(tokens[i]);
            if (normalized.Length != 0)
                AddPosting(normalized, docId);
        }
    }

    public void IndexDocument(string docId, IEnumerable<string> tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentNullException.ThrowIfNull(tokens);

        if (!TryAddDocument(docId, string.Empty))
            return;

        foreach (var token in tokens)
        {
            if (token == null)
                continue;
            var normalized = Normalize(token);
            if (normalized.Length != 0)
                AddPosting(normalized, docId);
        }
    }

    public SearchResult[] Search(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        var normalized = Normalize(term);
        if (normalized.Length == 0)
            return Array.Empty<SearchResult>();
        return Search(new[] { normalized });
    }

    public SearchResult[] Search(string[] terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Length == 0)
            return Array.Empty<SearchResult>();

        var normalizedTerms = new string[terms.Length];
        var normalizedCount = 0;
        for (var i = 0; i < terms.Length; i++)
        {
            if (terms[i] == null)
                continue;
            var normalized = Normalize(terms[i]);
            if (normalized.Length != 0)
                normalizedTerms[normalizedCount++] = normalized;
        }

        if (normalizedCount == 0)
            return Array.Empty<SearchResult>();

        var firstTermPostings = GetPostings(normalizedTerms[0], normalizedCount);
        if (normalizedCount == 1)
            return BuildResults(firstTermPostings);

        var matches = new SearchMatch[firstTermPostings.Length];
        var matchCount = 0;

        for (var i = 0; i < firstTermPostings.Length; i++)
        {
            var docId = firstTermPostings[i].DocId;
            var score = firstTermPostings[i].TermFrequency;
            var found = true;

            for (var termIndex = 1; termIndex < normalizedCount; termIndex++)
            {
                var frequency = FindFrequency(normalizedTerms[termIndex], docId);
                if (frequency == 0)
                {
                    found = false;
                    break;
                }
                score += frequency;
            }

            if (found)
                matches[matchCount++] = new SearchMatch(docId, score);
        }

        Array.Resize(ref matches, matchCount);
        SortMatches(matches);
        return BuildResults(matches);
    }

    public SearchResult[] Search(IEnumerable<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var values = new System.Collections.Generic.List<string>();
        foreach (var term in terms)
            values.Add(term);
        return Search(values.ToArray());
    }

    private readonly struct SearchMatch
    {
        public SearchMatch(string docId, int score)
        {
            DocId = docId;
            Score = score;
        }

        public string DocId { get; }
        public int Score { get; }
    }

    private Posting[] GetPostings(string term, int _)
    {
        var bucket = GetBucket(term);
        lock (bucket.Lock)
        {
            var entry = FindEntry(bucket, term);
            if (entry == null)
                return Array.Empty<Posting>();

            var copy = new Posting[entry.PostingCount];
            Array.Copy(entry.Postings, copy, entry.PostingCount);
            return copy;
        }
    }

    private int FindFrequency(string term, string docId)
    {
        var bucket = GetBucket(term);
        lock (bucket.Lock)
        {
            var entry = FindEntry(bucket, term);
            if (entry == null)
                return 0;

            for (var i = 0; i < entry.PostingCount; i++)
            {
                if (entry.Postings[i].DocId == docId)
                    return entry.Postings[i].TermFrequency;
            }
        }
        return 0;
    }

    private SearchResult[] BuildResults(Posting[] postings)
    {
        var matches = new SearchMatch[postings.Length];
        for (var i = 0; i < postings.Length; i++)
            matches[i] = new SearchMatch(postings[i].DocId, postings[i].TermFrequency);
        SortMatches(matches);
        return BuildResults(matches);
    }

    private SearchResult[] BuildResults(SearchMatch[] matches)
    {
        var results = new SearchResult[matches.Length];
        for (var i = 0; i < matches.Length; i++)
            results[i] = new SearchResult(matches[i].DocId, GetSnippet(matches[i].DocId), matches[i].Score);
        return results;
    }

    private static void SortMatches(SearchMatch[] matches)
    {
        for (var i = 1; i < matches.Length; i++)
        {
            var current = matches[i];
            var j = i - 1;
            while (j >= 0 &&
                   (matches[j].Score < current.Score ||
                    (matches[j].Score == current.Score && StringComparer.Ordinal.Compare(matches[j].DocId, current.DocId) > 0)))
            {
                matches[j + 1] = matches[j];
                j--;
            }
            matches[j + 1] = current;
        }
    }

    private string GetSnippet(string docId)
    {
        lock (_documentsLock)
        {
            for (var i = 0; i < _documentCount; i++)
            {
                if (_documents[i].DocId == docId)
                    return _documents[i].Snippet;
            }
        }
        return string.Empty;
    }

    private bool TryAddDocument(string docId, string snippet)
    {
        lock (_documentsLock)
        {
            for (var i = 0; i < _documentCount; i++)
            {
                if (_documents[i].DocId == docId)
                    return false;
            }

            if (_documentCount == _documents.Length)
            {
                var resized = new DocumentInfo[_documents.Length * 2];
                Array.Copy(_documents, resized, _documents.Length);
                _documents = resized;
            }

            _documents[_documentCount++] = new DocumentInfo(docId, snippet);
            return true;
        }
    }

    private void AddPosting(string term, string docId)
    {
        var bucket = GetBucket(term);
        lock (bucket.Lock)
        {
            var entry = FindEntry(bucket, term);
            if (entry == null)
            {
                entry = new Entry(term);
                entry.Next = bucket.First;
                bucket.First = entry;
            }

            for (var i = 0; i < entry.PostingCount; i++)
            {
                if (entry.Postings[i].DocId == docId)
                {
                    entry.Postings[i] = new Posting(docId, entry.Postings[i].TermFrequency + 1);
                    return;
                }
            }

            if (entry.PostingCount == entry.Postings.Length)
            {
                var resized = new Posting[entry.Postings.Length * 2];
                Array.Copy(entry.Postings, resized, entry.Postings.Length);
                entry.Postings = resized;
            }

            entry.Postings[entry.PostingCount++] = new Posting(docId, 1);
        }
    }

    private Bucket GetBucket(string term)
    {
        unchecked
        {
            var hash = 17;
            for (var i = 0; i < term.Length; i++)
                hash = (hash * 31) + char.ToLowerInvariant(term[i]);
            var index = (hash & 0x7FFFFFFF) % _buckets.Length;
            return _buckets[index];
        }
    }

    private static Entry? FindEntry(Bucket bucket, string term)
    {
        var entry = bucket.First;
        while (entry != null)
        {
            if (entry.Key == term)
                return entry;
            entry = entry.Next;
        }
        return null;
    }

    private static string Normalize(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var chars = token.ToCharArray();
        var length = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetterOrDigit(chars[i]))
                chars[length++] = char.ToLowerInvariant(chars[i]);
        }
        return length == 0 ? string.Empty : new string(chars, 0, length);
    }
}
