using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Structural pre-validation of a raw state document. System.Text.Json happily
/// deserializes null into list elements and into non-nullable string properties, and
/// those nulls would otherwise surface as NullReferenceExceptions (sanitized 500s)
/// deep inside resolution or schema validation. This pass turns each one into a
/// precise ValidationError instead. The protocol serializer never writes nulls
/// (WhenWritingNull), so any null found here is foreign input, never a legitimately
/// saved document — rejecting it does not conflict with liberal saved-report
/// acceptance.
/// </summary>
internal static class StateStructureValidator
{
    public static List<ValidationError> Collect(ReportState state)
    {
        var errors = new List<ValidationError>();
        CollectPipeline(state.Pipeline, "pipeline", errors);
        if (state.Shelf is not null)
        {
            foreach (var (key, stages) in state.Shelf)
                CollectPipeline(stages, $"shelf.{key}", errors);
        }
        return errors;
    }

    private static void CollectPipeline(List<PipelineStage>? stages, string path, List<ValidationError> errors)
    {
        if (stages is null) return;
        for (var i = 0; i < stages.Count; i++)
        {
            var stagePath = $"{path}[{i}]";
            if (stages[i] is not { } stage)
            {
                errors.Add(new ValidationError(stagePath, "pipeline stages must not be null"));
                continue;
            }
            CollectShape(stage.Shape, $"{stagePath}.shape", errors);
            CollectLayer(stage.Layer, $"{stagePath}.layer", errors);
        }
    }

    private static void CollectShape(StageShape? shape, string path, List<ValidationError> errors)
    {
        if (shape is null) return;
        CollectStrings(shape.By, $"{path}.by", errors);
        CollectStrings(shape.Cols, $"{path}.cols", errors);
        CollectRules(shape.Values, $"{path}.values", errors, (metric, metricPath) =>
        {
            RequireValue(metric.Id, $"{metricPath}.id", errors);
            RequireValue(metric.Col, $"{metricPath}.col", errors);
        });
    }

    private static void CollectLayer(StageLayer? layer, string path, List<ValidationError> errors)
    {
        if (layer is null) return;
        CollectStrings(layer.Columns, $"{path}.columns", errors);
        CollectStrings(layer.Breaks, $"{path}.breaks", errors);
        CollectRules(layer.Computed, $"{path}.computed", errors, (rule, rulePath) =>
        {
            RequireValue(rule.Id, $"{rulePath}.id", errors);
            RequireValue(rule.Expr, $"{rulePath}.expr", errors);
        });
        CollectRules(layer.Filters, $"{path}.filters", errors, (rule, rulePath) =>
            RequireValue(rule.Expr, $"{rulePath}.expr", errors));
        CollectRules(layer.Sorts, $"{path}.sorts", errors, (rule, rulePath) =>
            RequireValue(rule.Col, $"{rulePath}.col", errors));
        CollectRules(layer.Highlights, $"{path}.highlights", errors, (rule, rulePath) =>
        {
            RequireValue(rule.Id, $"{rulePath}.id", errors);
            RequireValue(rule.Scope, $"{rulePath}.scope", errors);
            RequireValue(rule.Expr, $"{rulePath}.expr", errors);
        });
        CollectRules(layer.Aggregates, $"{path}.aggregates", errors, (rule, rulePath) =>
            RequireValue(rule.Col, $"{rulePath}.col", errors));
    }

    private static void CollectRules<T>(
        List<T>? rules,
        string path,
        List<ValidationError> errors,
        Action<T, string> check)
        where T : class
    {
        if (rules is null) return;
        for (var i = 0; i < rules.Count; i++)
        {
            var rulePath = $"{path}[{i}]";
            if (rules[i] is not { } rule)
            {
                errors.Add(new ValidationError(rulePath, "list elements must not be null"));
                continue;
            }
            check(rule, rulePath);
        }
    }

    private static void CollectStrings(List<string>? values, string path, List<ValidationError> errors)
    {
        if (values is null) return;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is null)
                errors.Add(new ValidationError($"{path}[{i}]", "list elements must not be null"));
        }
    }

    private static void RequireValue(string? value, string path, List<ValidationError> errors)
    {
        if (value is null)
            errors.Add(new ValidationError(path, "a value is required (null is not accepted)"));
    }
}
