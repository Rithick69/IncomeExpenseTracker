using System.Collections.Generic;
using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Planning
{
    /// <summary>
    /// Provides planning and forecasting capabilities for financial goals and simulations.
    /// This interface will route all database queries through IDatabaseService when implemented.
    /// Currently supports mocked data for UI development.
    /// </summary>
    public interface IPlanningService
    {
        /// <summary>
        /// Fetches all active financial goals with their linked tags and progress tracking.
        /// </summary>
        /// <returns>List of user-defined financial goals</returns>
        Task<IReadOnlyList<FinancialGoalDto>> GetActiveGoalsAsync();

        /// <summary>
        /// Creates a new financial goal linked to a specific tag or category.
        /// </summary>
        /// <param name="goalName">Name/description of the goal</param>
        /// <param name="targetAmount">Target amount to achieve</param>
        /// <param name="linkedTagId">Optional tag ID to track progress against</param>
        /// <returns>The newly created goal ID</returns>
        Task<long> CreateGoalAsync(string goalName, decimal targetAmount, long? linkedTagId);

        /// <summary>
        /// Updates progress on a financial goal.
        /// Recalculates completion percentage based on linked tag transactions.
        /// </summary>
        /// <param name="goalId">ID of the goal to update</param>
        /// <returns>Updated goal with recalculated progress</returns>
        Task<FinancialGoalDto> UpdateGoalProgressAsync(long goalId);

        /// <summary>
        /// Runs a "What-If" financial simulation based on user-defined parameters.
        /// Simulates future cash flow, savings projections, and goal achievement timelines.
        /// </summary>
        /// <param name="monthlyIncome">Projected monthly income</param>
        /// <param name="monthlyExpense">Projected monthly expense</param>
        /// <param name="simulationMonths">Number of months to project forward</param>
        /// <returns>Simulation results with projected balances and goal timelines</returns>
        Task<SimulationResultDto> RunFinancialSimulationAsync(decimal monthlyIncome, decimal monthlyExpense, int simulationMonths);
    }

    /// <summary>
    /// Data transfer object representing a financial goal.
    /// </summary>
    public record FinancialGoalDto
    {
        public long Id { get; init; }
        public string GoalName { get; init; } = string.Empty;
        public decimal TargetAmount { get; init; }
        public decimal CurrentAmount { get; init; }
        public double CompletionPercentage { get; init; }
        public long? LinkedTagId { get; init; }
        public string? LinkedTagName { get; init; }
    }

    /// <summary>
    /// Data transfer object representing a financial simulation result.
    /// </summary>
    public record SimulationResultDto
    {
        public List<MonthlyProjection> Projections { get; init; } = new();
        public decimal FinalBalance { get; init; }
        public int MonthsToGoal { get; init; }
    }

    /// <summary>
    /// Data transfer object representing a single month's projection in a simulation.
    /// </summary>
    public record MonthlyProjection(int Month, decimal Income, decimal Expense, decimal Balance);
}
