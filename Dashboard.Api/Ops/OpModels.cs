namespace Dashboard.Api.Ops;

public sealed record OpDecision(
    string Category,
    decimal CurrentSpend,
    decimal Suggested,
    decimal Cap,
    string Advice,
    decimal? Delta = null,
    double? UtilizationPercent = null);

public sealed record OpMetric(string Label, string Value);

public sealed record OpSolveResult(
    string Template,
    string Title,
    string Summary,
    decimal Objective,
    string ObjectiveLabel,
    IReadOnlyList<OpDecision> Decisions,
    bool Feasible,
    IReadOnlyList<OpMetric>? Metrics = null);

public sealed record OpSolveRequest(
    string Template,
    decimal? YearLeftoverTarget,
    decimal? ExtraCutPercent,
    string? WhatIfCategory = null,
    decimal? WhatIfChangePercent = null,
    decimal? MustKeepFloorPercent = null,
    int? HorizonDays = null);
