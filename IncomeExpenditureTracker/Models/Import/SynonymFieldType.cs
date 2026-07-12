namespace IncomeExpenditureTracker.Models
{

    public enum FieldCategory
    {
        TransactionColumn = 1,
        AccountMetadata = 2
    }

    // Strictly defines table column headers (to be prefixed with Col:)
    // Ensure unique names for each column type to avoid ambiguity during mapping.
    public enum TransactionColumnField
    {
        DATE,
        DESCRIPTION,
        DEBIT,
        CREDIT,
        AMOUNT,
    }

    // Strictly defines statement/account header data (to be prefixed with Meta:)
    // Ensure unique names for each field type to avoid ambiguity during mapping.
    public enum MetadataField
    {
        ACCOUNT_NUMBER,
        CARD_NUMBER,
        ACCOUNT_TYPE,
        CURRENCY,
        CREDIT_LIMIT,
        ENTITY_NAME
    }
}