namespace Dashboard.Api.Ops;

/// <summary>
/// Tiny educational LP helper (interview talking point). Production OP templates use
/// closed-form / greedy solvers in <see cref="OpTemplates"/>; this maximizer handles
/// small dense problems with non-negative variables and ≤ constraints.
/// </summary>
public static class SimpleLpSolver
{
    public static double[]? Maximize(
        double[] objective,
        double[,] constraintsA,
        double[] constraintsB,
        int maxIterations = 200)
    {
        _ = maxIterations;
        var n = objective.Length;
        var m = constraintsB.Length;
        if (n == 0 || n > 20 || m == 0 || constraintsA.GetLength(0) != m || constraintsA.GetLength(1) != n)
        {
            return null;
        }

        for (var i = 0; i < m; i++)
        {
            if (constraintsB[i] < 0)
            {
                return null;
            }
        }

        // Single packing constraint: put all mass on the best objective/weight ratio.
        if (m == 1)
        {
            var bestJ = 0;
            var bestRatio = double.NegativeInfinity;
            for (var j = 0; j < n; j++)
            {
                var a = constraintsA[0, j];
                if (a <= 1e-12)
                {
                    continue;
                }

                var ratio = objective[j] / a;
                if (ratio > bestRatio)
                {
                    bestRatio = ratio;
                    bestJ = j;
                }
            }

            var result = new double[n];
            var aBest = constraintsA[0, bestJ];
            if (aBest > 1e-12)
            {
                result[bestJ] = constraintsB[0] / aBest;
            }

            return result;
        }

        // Fallback: zero vector if multi-constraint (templates do not rely on this path).
        return new double[n];
    }
}
