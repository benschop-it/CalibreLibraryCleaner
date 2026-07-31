using CalibreLibraryCleaner.Domain.Assessments;

namespace CalibreLibraryCleaner.Application.Assessments.Pdf;

public sealed class PdfPageSamplingPolicy
{
    private readonly int _maximumSampledPages = PdfInspectionLimits.V1.MaximumSampledPages;

    public static PdfPolicyVersion Version { get; } = new("pdf-sampling/1.0.0");

    public IReadOnlyList<int> Select(int pageCount, IReadOnlyList<int>? outlineTargetPages = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        if (pageCount > PdfInspectionLimits.V1.MaximumPages)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount), "PDF page count exceeds the V1 hard limit.");
        }

        if (pageCount <= _maximumSampledPages)
        {
            return Enumerable.Range(1, pageCount).ToArray();
        }

        SortedSet<int> selected = [];
        AddRange(selected, 1, 5, pageCount);
        AddRange(selected, pageCount - 2, 3, pageCount);
        int outlineCount = 0;
        HashSet<int> seenOutlineTargets = [];
        foreach (int target in outlineTargetPages ?? [])
        {
            if (outlineCount >= 32)
            {
                break;
            }

            if (target < 1 || target > pageCount || !seenOutlineTargets.Add(target))
            {
                continue;
            }

            selected.Add(target);
            if (target < pageCount)
            {
                selected.Add(target + 1);
            }

            outlineCount++;
        }

        int cap = _maximumSampledPages;
        int remaining = cap - selected.Count;
        for (int index = 0; index < remaining && selected.Count < cap; index++)
        {
            int page = 1 + (int)((2L * index + 1) * (pageCount - 1) / (2L * remaining));
            selected.Add(page);
        }

        while (selected.Count < cap)
        {
            int[] ordered = selected.ToArray();
            int candidate = 0;
            int largestGap = 0;
            for (int index = 1; index < ordered.Length; index++)
            {
                int gap = ordered[index] - ordered[index - 1];
                if (gap > largestGap)
                {
                    largestGap = gap;
                    candidate = ordered[index - 1] + gap / 2;
                }
            }

            if (largestGap <= 1 || !selected.Add(candidate))
            {
                break;
            }
        }

        return selected.Take(cap).ToArray();
    }

    private static void AddRange(SortedSet<int> target, int start, int count, int pageCount)
    {
        for (int offset = 0; offset < count; offset++)
        {
            int page = start + offset;
            if (page is >= 1 && page <= pageCount)
            {
                target.Add(page);
            }
        }
    }
}
