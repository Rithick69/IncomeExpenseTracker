namespace IncomeExpenditureTracker.Models;

// FieldScoringRule defines a rule for scoring a specific field or set of fields.
// Each rule specifies the target keys to look for and the maximum weight (points) that can be awarded if the field is present and valid.
public record FieldScoringRule(string[] TargetKeys, double MaxWeight);