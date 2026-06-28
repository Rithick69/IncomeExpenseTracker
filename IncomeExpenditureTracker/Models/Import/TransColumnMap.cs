// This file defines the TransColumnMap class, which is used to map the columns of an imported CSV file to the properties of the Transaction model.
public class TransColumnMap
{
    public int HeaderRow { get; set; }
    public int DateColumn { get; set; }

    public int DescriptionColumn { get; set; }

    public int DebitColumn { get; set; }

    public int CreditColumn { get; set; }

    public int AmountColumn { get; set; } // For single amount column files
}