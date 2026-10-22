namespace Arkive.Core.Outfits;

/// <summary>A k-d tree over points of any dimension, for nearest-neighbour and radius queries.</summary>
internal sealed class OutfitSwapKdTree
{
    private readonly float[] _points;
    private readonly int _dimensions;
    private readonly int[] _order;
    private readonly int[] _axis;

    public int Count { get; }

    /// <param name="points">Point coordinates one after another (<paramref name="dimensions"/> per point).</param>
    public OutfitSwapKdTree(float[] points, int dimensions)
    {
        _points = points;
        _dimensions = dimensions;
        Count = points.Length / dimensions;
        _order = Enumerable.Range(0, Count).ToArray();
        _axis = new int[Count];
        Build(0, Count);
    }

    public float Coordinate(int point, int dimension) => _points[point * _dimensions + dimension];

    private void Build(int start, int end)
    {
        if (end - start <= 1) return;
        // Split on the widest dimension at the median.
        var widest = 0;
        var width = -1f;
        for (var d = 0; d < _dimensions; d++)
        {
            float low = float.MaxValue, high = float.MinValue;
            for (var i = start; i < end; i++)
            {
                var value = _points[_order[i] * _dimensions + d];
                if (value < low) low = value;
                if (value > high) high = value;
            }
            if (high - low > width) { width = high - low; widest = d; }
        }
        var middle = (start + end) / 2;
        Select(start, end - 1, middle, widest);
        _axis[middle] = widest;
        Build(start, middle);
        Build(middle + 1, end);
    }

    private void Select(int left, int right, int k, int axis)
    {
        while (left < right)
        {
            var pivot = _points[_order[(left + right) / 2] * _dimensions + axis];
            int i = left, j = right;
            while (i <= j)
            {
                while (_points[_order[i] * _dimensions + axis] < pivot) i++;
                while (_points[_order[j] * _dimensions + axis] > pivot) j--;
                if (i <= j) { (_order[i], _order[j]) = (_order[j], _order[i]); i++; j--; }
            }
            if (k <= j) right = j;
            else if (k >= i) left = i;
            else return;
        }
    }

    /// <summary>The <paramref name="k"/> nearest points to <paramref name="query"/>, nearest first.</summary>
    public int Nearest(ReadOnlySpan<float> query, int k, Span<int> indices, Span<float> distances)
    {
        k = Math.Min(k, Count);
        var found = 0;
        distances[..k].Fill(float.MaxValue);
        Search(0, Count, query, k, indices, distances, ref found);
        for (var i = 0; i < found; i++) distances[i] = MathF.Sqrt(distances[i]);
        return found;
    }

    private void Search(int start, int end, ReadOnlySpan<float> query, int k, Span<int> indices, Span<float> squared, ref int found)
    {
        if (start >= end) return;
        var middle = (start + end) / 2;
        var point = _order[middle];
        var distance = 0f;
        for (var d = 0; d < _dimensions; d++)
        {
            var delta = _points[point * _dimensions + d] - query[d];
            distance += delta * delta;
        }
        if (found < k || distance < squared[found - 1])
        {
            var at = found < k ? found++ : found - 1;
            while (at > 0 && squared[at - 1] > distance)
            {
                squared[at] = squared[at - 1];
                indices[at] = indices[at - 1];
                at--;
            }
            squared[at] = distance;
            indices[at] = point;
        }
        var axis = _axis[middle];
        var split = query[axis] - _points[point * _dimensions + axis];
        var (near, far) = split < 0 ? ((start, middle), (middle + 1, end)) : ((middle + 1, end), (start, middle));
        Search(near.Item1, near.Item2, query, k, indices, squared, ref found);
        if (found < k || split * split < squared[found - 1]) Search(far.Item1, far.Item2, query, k, indices, squared, ref found);
    }

    /// <summary>Every point within <paramref name="radius"/> of <paramref name="query"/>.</summary>
    public void Within(ReadOnlySpan<float> query, float radius, List<int> result)
    {
        result.Clear();
        Within(0, Count, query, radius * radius, result);
    }

    private void Within(int start, int end, ReadOnlySpan<float> query, float squared, List<int> result)
    {
        if (start >= end) return;
        var middle = (start + end) / 2;
        var point = _order[middle];
        var distance = 0f;
        for (var d = 0; d < _dimensions; d++)
        {
            var delta = _points[point * _dimensions + d] - query[d];
            distance += delta * delta;
        }
        if (distance <= squared) result.Add(point);
        var axis = _axis[middle];
        var split = query[axis] - _points[point * _dimensions + axis];
        if (split <= 0 || split * split <= squared) Within(start, middle, query, squared, result);
        if (split >= 0 || split * split <= squared) Within(middle + 1, end, query, squared, result);
    }
}
