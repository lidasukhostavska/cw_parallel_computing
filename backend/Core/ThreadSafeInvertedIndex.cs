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

    public ThreadSafeInvertedIndex(int bucketCount = 131072)
    {
        if (bucketCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(bucketCount));
        if ((bucketCount & (bucketCount - 1)) != 0)
            throw new ArgumentException("Bucket count must be a power of two.", nameof(bucketCount));

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

        var localTerms = new string[64];
        var localCounts = new int[64];
        var localUniqueCount = 0;

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
                var token = text.AsSpan(start, end - start);
                var slot = FindLocalTerm(localTerms, token);

                if (slot >= 0)
                {
                    localCounts[slot]++;
                }
                else
                {
                    if ((localUniqueCount + 1) * 4 >= localTerms.Length * 3)
                        GrowLocalTable(ref localTerms, ref localCounts);

                    var normalized = CreateNormalizedTerm(token);
                    if (normalized.Length != 0)
                    {
                        slot = FindEmptyLocalSlot(localTerms, normalized);
                        localTerms[slot] = normalized;
                        localCounts[slot] = 1;
                        localUniqueCount++;
                    }
                }
            }

            start = end;
        }

        for (var i = 0; i < localTerms.Length; i++)
        {
            if (localTerms[i] != null)
                AddPostingWithFrequency(localTerms[i], docId, localCounts[i]);
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

    public SearchResult[] SearchAny(string[] terms, int topK = 20)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (topK <= 0 || terms.Length == 0)
            return Array.Empty<SearchResult>();

        var uniqueTerms = new string[terms.Length];
        var uniqueTermCount = 0;

        for (var i = 0; i < terms.Length; i++)
        {
            if (terms[i] == null)
                continue;

            var normalized = Normalize(terms[i]);
            if (normalized.Length == 0)
                continue;

            var alreadyAdded = false;
            for (var j = 0; j < uniqueTermCount; j++)
            {
                if (uniqueTerms[j] == normalized)
                {
                    alreadyAdded = true;
                    break;
                }
            }

            if (!alreadyAdded)
                uniqueTerms[uniqueTermCount++] = normalized;
        }

        if (uniqueTermCount == 0)
            return Array.Empty<SearchResult>();

        var matches = new SearchMatch[4];
        var matchCount = 0;

        for (var termIndex = 0; termIndex < uniqueTermCount; termIndex++)
        {
            var postings = GetPostings(uniqueTerms[termIndex], uniqueTermCount);
            for (var postingIndex = 0; postingIndex < postings.Length; postingIndex++)
            {
                var posting = postings[postingIndex];
                var matchIndex = -1;

                for (var i = 0; i < matchCount; i++)
                {
                    if (matches[i].DocId == posting.DocId)
                    {
                        matchIndex = i;
                        break;
                    }
                }

                if (matchIndex >= 0)
                {
                    var existing = matches[matchIndex];
                    matches[matchIndex] = new SearchMatch(
                        existing.DocId,
                        existing.Score + posting.TermFrequency,
                        existing.DistinctTermCount + 1);
                }
                else
                {
                    if (matchCount == matches.Length)
                    {
                        var resized = new SearchMatch[matches.Length * 2];
                        for (var i = 0; i < matchCount; i++)
                            resized[i] = matches[i];
                        matches = resized;
                    }

                    matches[matchCount++] = new SearchMatch(
                        posting.DocId,
                        posting.TermFrequency,
                        1);
                }
            }
        }

        var ranked = new SearchMatch[matchCount];
        for (var i = 0; i < matchCount; i++)
        {
            var match = matches[i];
            var distinctTermBonus =
                match.DistinctTermCount > 1
                    ? (match.DistinctTermCount - 1) * 5
                    : 0;
            ranked[i] = new SearchMatch(
                match.DocId,
                match.Score + distinctTermBonus,
                match.DistinctTermCount);
        }

        SortMatches(ranked);

        var resultCount = ranked.Length < topK ? ranked.Length : topK;
        var results = new SearchResult[resultCount];
        for (var i = 0; i < resultCount; i++)
        {
            results[i] = new SearchResult(
                ranked[i].DocId,
                GetSnippet(ranked[i].DocId),
                ranked[i].Score);
        }

        return results;
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
            DistinctTermCount = 1;
        }

        public SearchMatch(string docId, int score, int distinctTermCount)
        {
            DocId = docId;
            Score = score;
            DistinctTermCount = distinctTermCount;
        }

        public string DocId { get; }
        public int Score { get; }
        public int DistinctTermCount { get; }
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

    private static int FindLocalTerm(string[] localTerms, ReadOnlySpan<char> token)
    {
        var capacity = localTerms.Length;
        var slot = HashLocalSpan(token) & (capacity - 1);

        while (localTerms[slot] != null)
        {
            if (LocalTermEquals(localTerms[slot], token))
                return slot;
            slot = (slot + 1) & (capacity - 1);
        }

        return -1;
    }

    private static int FindEmptyLocalSlot(string[] localTerms, string term)
    {
        var capacity = localTerms.Length;
        var slot = HashLocalString(term) & (capacity - 1);

        while (localTerms[slot] != null)
            slot = (slot + 1) & (capacity - 1);

        return slot;
    }

    private static void GrowLocalTable(
        ref string[] localTerms,
        ref int[] localCounts)
    {
        var resizedTerms = new string[localTerms.Length * 2];
        var resizedCounts = new int[resizedTerms.Length];

        for (var i = 0; i < localTerms.Length; i++)
        {
            if (localTerms[i] == null)
                continue;

            var capacity = resizedTerms.Length;
            var slot = HashLocalString(localTerms[i]) & (capacity - 1);
            while (resizedTerms[slot] != null)
                slot = (slot + 1) & (capacity - 1);

            resizedTerms[slot] = localTerms[i];
            resizedCounts[slot] = localCounts[i];
        }

        localTerms = resizedTerms;
        localCounts = resizedCounts;
    }

    private static string CreateNormalizedTerm(ReadOnlySpan<char> token)
    {
        var characters = new char[token.Length];
        var length = 0;

        for (var i = 0; i < token.Length; i++)
        {
            if (char.IsLetterOrDigit(token[i]))
                characters[length++] = char.ToLowerInvariant(token[i]);
        }

        return length == 0 ? string.Empty : new string(characters, 0, length);
    }

    private static bool LocalTermEquals(string term, ReadOnlySpan<char> token)
    {
        if (term.Length != token.Length)
            return false;

        for (var i = 0; i < term.Length; i++)
        {
            if (term[i] != char.ToLowerInvariant(token[i]))
                return false;
        }

        return true;
    }

    private static int HashLocalSpan(ReadOnlySpan<char> value)
    {
        unchecked
        {
            var hash = 17;
            for (var i = 0; i < value.Length; i++)
                hash = (hash * 31) + char.ToLowerInvariant(value[i]);
            return hash;
        }
    }

    private static int HashLocalString(string value)
    {
        unchecked
        {
            var hash = 17;
            for (var i = 0; i < value.Length; i++)
                hash = (hash * 31) + char.ToLowerInvariant(value[i]);
            return hash;
        }
    }

    private void AddPosting(string term, string docId)
    {
        AddPostingWithFrequency(term, docId, 1);
    }

    private void AddPostingWithFrequency(string term, string docId, int frequency)
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
                    entry.Postings[i] = new Posting(
                        docId,
                        entry.Postings[i].TermFrequency + frequency);
                    return;
                }
            }

            if (entry.PostingCount == entry.Postings.Length)
            {
                var resized = new Posting[entry.Postings.Length * 2];
                Array.Copy(entry.Postings, resized, entry.Postings.Length);
                entry.Postings = resized;
            }

            entry.Postings[entry.PostingCount++] = new Posting(docId, frequency);
        }
    }

    private Bucket GetBucket(string term)
    {
        unchecked
        {
            var hash = 17;
            for (var i = 0; i < term.Length; i++)
                hash = (hash * 31) + char.ToLowerInvariant(term[i]);
            var index = hash & (_buckets.Length - 1);
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
