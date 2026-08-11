using System;

namespace IncomeExpenditureTracker.Models;

public class Entity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Country { get; set; } = "";

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}