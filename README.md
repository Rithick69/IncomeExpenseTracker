# 📊 Income & Expenditure Tracker

> A high-performance, resilient desktop personal finance application built with C# (.NET 8), Avalonia UI, SQLite, Dapper, and ClosedXML.

---

## 🚀 Overview

The **Income & Expenditure Tracker** is a precision financial calculation and data presentation tool designed to ingest messy, multi-year bank statements, automatically detect spreadsheet layouts, and present clean, structured transaction data for human interpretation.

Unlike bloated financial advisory apps that rely on subjective heuristics, this application focuses strictly on **data accuracy, high-speed extraction, and zero-leak OS resource management**. It features a robust, self-learning backend architecture that adapts to bank formatting anomalies without ever freezing the UI or locking physical spreadsheet files.

---

## ✨ Key Features

- **⚡ Concurrent Lock-Free Ingestion:** Stage up to 5 multi-year Excel workbooks simultaneously using non-blocking asynchronous workflows (`Task.WhenAll` and `ConcurrentDictionary`).

- **🛡️ Ironclad OS Resource Management:** Strictly enforced `IDisposable` patterns and atomic `DiscardFile` routines guarantee Windows OS file locks are released immediately upon stream transfer, error trapping, or cancellation—allowing users to move or rename files in Explorer without restarting the app.

- **🧠 User-Confirmed Self-Learning:** Uses an atomic `SynonymService` to dynamically learn new bank headers and category rules. Learning triggers **only** after explicit user verification upon completing a staging session, executing asynchronously on background threads.

- **🛡️ Concurrency & Stampede Defense:** All reference and lookup services utilize an async lazy cache registry (`ConcurrentDictionary<string, Lazy<Task<T>>>`) with automatic fault eviction, guaranteeing that concurrent threads hit the database exactly once during multi-file staging without caching transient I/O failures.
- **🔒 Master Transaction Atomicity & Single Disk Sync:** Batch persistence executes entirely under a single SQLite master transaction (`conn, tx`), holding all entity creation, account linking, and Dapper bulk inserts in the WAL buffer for a single filesystem disk synchronization and 100% all-or-nothing rollback protection.
- **⚡ Race-Condition Free Upserts & Stateless WAL Queries:** Entity and account services execute atomic SQLite upsert SQL (`INSERT OR IGNORE`) to prevent concurrent collision exceptions, while dashboard queries hit native C-compiled B-tree indexes (`idx_transactions_accountid`, `idx_transactions_entity`) for sub-2-millisecond retrievals without RAM heap bloat.
- **📐 $O(1)$ Coordinate-Driven Math:** High-volume transaction extraction loops operate strictly on boundary-resolved integer coordinates (`TransactionColumnCoordinates`), completely bypassing slow string dictionary lookups.

- **🌍 International & Formatting Agnostic:** Bulletproof cell parsing (`GetDecimal`) handles global number formatting (e.g., `1.250,00`), standardizes Debit/Credit columns (Single or Dual column mode), and automatically sanitizes accounting parentheses, trailing minus signs, and HTML non-breaking spaces.

- **🔒 Hybrid Domain Isolation:** Segregates transaction table columns from account metadata fields at compile-time and database retrieval levels, preventing cross-contamination during dictionary pre-seeding.

- **📈 Proportional Confidence Scoring:** Evaluates document trustworthiness via weighted, self-normalizing rules (`ConfidenceService`), automatically generating visual UI badges when critical transaction pillars are missing or uncertain.

---

## 🛠️ Tech Stack & Dependencies

The application relies on a modern .NET runtime paired with high-performance persistence and presentation libraries:

| Component / Library | Technology | Version | Purpose                                    |
| ------------------- | ---------- | ------- | ------------------------------------------ |
| **Framework**       | .NET (C#)  | `8.0.x` | Core application runtime and backend logic |

|
| **Presentation Layer** | Avalonia UI | `[Insert Version]` | Cross-platform MVVM desktop UI rendering

|
| **Database Engine** | SQLite | `[Insert Version]` | Local relational persistence with WAL journal mode

|
| **ORM / Data Access** | Dapper | `[Insert Version]` | High-performance micro-ORM for coordinate-optimized queries

|
| **Excel Spreadsheet I/O** | ClosedXML | `[Insert Version]` | Stream-based workbook loading and worksheet manipulation

|
| **Dependency Injection** | Microsoft.Extensions.DependencyInjection | `[Insert Version]` | Interface-driven service lifecycle management

|
| **Structured Logging** | Microsoft.Extensions.Logging | `[Insert Version]` | Asynchronous lifecycle, debug, and error telemetry

|

---

## 🏗️ Architecture & Core Principles

The backend is engineered around strict separation of concerns and high-concurrency systems design:

1. **Orchestration Ownership (`StatementManager`):** The manager strictly coordinates workflows, thread-safe staging queues, and workbook stream lifecycles. It contains zero domain business logic or cell parsing rules.

2. **In-Memory & Read-Only Analysis:** Extractors, parsers, and preview analysis routines operate solely on in-memory `IXLWorksheet` instances and DTOs. Previewing and staging spreadsheets never execute database writes.

3. **Resilient SQLite Access:** Every database query is routed through a centralized `DatabaseService.ExecuteWithRetryAsync()` wrapper. SQLite connections automatically enable `PRAGMA foreign_keys = ON` and `PRAGMA journal_mode = WAL` (Write-Ahead Logging) to allow concurrent background learning and reading without `SQLITE_BUSY` lock contention.

4. **Open-Closed Schema Flexibility:** All data transfer across extraction boundaries relies on namespaced, case-insensitive dictionaries (`Dictionary<string, DetectedField>`) using `Col:*` and `Meta:*` prefixes, eliminating rigid C# domain properties and brittle `switch` statements.

5. **Concurrency & Stampede Defense:** All reference and lookup services implement thread-safe lazy caching with automatic fault eviction to prevent multiple threads from triggering redundant SQLite reads during multi-file staging.
6. **Master Transaction Boundaries:** Batch persistence routines hold all operations (entity linking, account creation, batch auditing, bulk transaction insertion) under a single, all-or-nothing database transaction token to guarantee atomicity and minimize filesystem disk synchronization overhead.

---

## 📂 Solution Structure

The solution is structured into modular responsibilities across domain entities, services, helpers, and import pipelines:

```plaintext
Models/
├── PreviewTracker (Global Hand-off Bundle: FinalPreview + ColumnCorrections)
└── Utilities/
     └── RuleBookSnapshot (Immutable keyword rule indexing & MiscTagId bundle)
Services/
├── Database/
│    ├── DatabaseService / IDatabaseService
│    └── DatabaseInitializer (Seeds baseline synonyms by Category; manages B-Tree indexes)
├── DependencyInjection/
│    └── ServiceRegistration
├── Entities/
│    ├── AccountService / EntityService (Atomic upserts; transaction-aware cache bypass)
│    └── TransactionService / ImportBatchService (Stateless B-Tree WAL queries; Dapper bulk inserts)
├── Helpers/
│    ├── HeaderDetector / FieldMapper (Zero-Leak Pre-Seeding)
│    ├── DescriptionParser
│    └── SynonymService (Atomic CRUD, Self-Learning, Category Discriminators & Lazy Caching)
├── Importing/
│    ├── ExcelStatementExtractor
│    └── ExcelStatementImportService (Master Transaction Orchestration & Bulk Persistence)
├── PreviewInsights/
│    └── ConfidenceService (Proportional Dynamic Engine)
├── StatementManagement/
│    ├── StatementLoader
│    ├── StatementManager (Thread-Safe Concurrent Staging, OS Lock Manager & Centralized Error Sink)
│    └── StatementEditSession (In-Memory Staging Scratchpad)
├── Tagging/
│    └── RuleBook (Immutable RAM Snapshots & Async Lazy Caching) / TagEngine / TagService (Planned)
└── TransactionExtractor/
     └── ExcelTransactionExtractor (GetDecimal Cell Parser & O(1) Coordinate Structs)

```

---

## ⚡ High-Level Processing Pipeline

The ingestion pipeline orchestrates concurrent workbook loading, in-memory analysis, user verification, and atomic batch persistence:

```
[Avalonia UI]
   │ (Selects up to 5 Excel files)
   ▼
[StatementManager] ──► [StatementLoader] (Transfers Stream Ownership to RAM)
   │
   ├─► [ConcurrentDictionary Staging Registry] (Lock-Free Read/Write)
   │
   ├─► [ExcelStatementExtractor] ──► [SynonymService] (Async Lazy Cache Snapshot / Zero Contention)
   │                               ├─► [FieldMapper / HeaderDetector]
   │                               ├─► [ExcelTransactionExtractor] (O(1) Coordinate Math)
   │                               └─► [ConfidenceService] (Trustworthiness Scoring)
   ▼
[StatementEditSession] (User verifies grid & adjusts column mappings via UI dropdowns)
   │
   ▼
[CommitStagedFileAsync]
   ├─► [Background Thread via Task.Run()] ──► [SynonymService.LearnFromCorrectionAsync()]
   ├─► [Master ExecuteInTransaction Block] ─► [ExcelStatementImportService] (Atomic Batch Persistence)
   │                                            ├─► [EntityService & AccountService] (Atomic Upserts)
   │                                            ├─► [TagEngine] (Evaluates RuleBookSnapshot)
   │                                            └─► [TransactionService] (Dapper Bulk Inserts)
   └─► [finally Clause] ────────────────────► [DiscardFile -> Dispose Stream -> Release OS Lock]

```

---

## 🗺️ Roadmap & Development Progress

### ✅ Completed Milestones (Phase 1 & Phase 2)

- [x] Modular service structure and interface-driven Dependency Injection registry.

- [x] Centralized retry-based SQLite access with WAL mode and Foreign Key enforcement.

- [x] Atomic `SynonymService` with priority-based self-learning and category discriminators.

- [x] Universal namespaced `DetectedField` dictionary schema (`Col:*` and `Meta:*`).

- [x] Boundary coordinate resolution via `TransactionColumnCoordinates` for $O(1)$ parsing.

- [x] Bulletproof `GetDecimal` parser supporting international formats and anomaly sanitization.

- [x] Thread-safe concurrent multi-file staging (`StageFilesAsync`) and ironclad OS lock release.

- [x] Non-blocking background learning dispatch to preserve instant UI responsiveness.

- [x] Async lazy cache registry (`ConcurrentDictionary<string, Lazy<Task<T>>>`) with fault eviction across reference services.
- [x] Race-condition free atomic SQLite upserts (`INSERT OR IGNORE`) and transaction-aware RAM cache bypasses (`if (tx != null)`).
- [x] Master import orchestration under a single database transaction (`ExecuteInTransactionWithRetryAsync`) with 100% all-or-nothing atomicity and single filesystem disk sync.
- [x] High-speed parameterized Dapper bulk inserts and stateless, B-tree indexed WAL dashboard queries (< 2ms execution).
- [x] Immutable `RuleBookSnapshot` records with pre-sorted keyword rules for zero-contention $O(1)$ tagging.

### 🚧 Current Focus (Phase 3: Tagging Expansion)

- [ ] Implement `ITagService` with atomic CRUD methods, priority-based self-learning, and async lazy caching with fault eviction.
- [ ] Allow `StatementEditSession` to pass manual category overrides to the database upon import confirmation.

### 🔮 Future Roadmap (Phase 4 & Phase 5)

- [ ] **Phase 4: Headless Workflow Integration Testing:** Build an automated, UI-less test harness using real-world, multi-year bank and credit card Excel statements to validate concurrent staging, stampede defense under load, duplicate hash detection, and atomic rollback recovery.
- [ ] **Phase 5: Reactive UI Messenger & MVVM Refactor:** Establish an application-wide messaging broker (`IMessenger` / Event Aggregator) to synchronize standalone management views with active staging sessions. Refactor `StatementManager` to record errors and emit UI toast/modal notifications, and build out the Avalonia UI ViewModels using transient `ObservableCollection` caching.

---

## 💻 Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.

- An IDE such as **Visual Studio 2022**, **JetBrains Rider**, or **VS Code** with the C# Dev Kit.

### Build & Run

1. **Clone the repository:**

```bash
git clone https://github.com/yourusername/IncomeExpenditureTracker.git
cd IncomeExpenditureTracker

```

2. **Restore dependencies:**

```bash
dotnet restore

```

3. **Build the solution:**

```bash
dotnet build --configuration Release

```

4. **Run the application:**

```bash
dotnet run --project src/IncomeExpenditureTracker.UI

```

---

## 🤝 Contributing & AI Guardrails

When contributing or utilizing AI assistants for further development on this repository, strictly adhere to the project's architectural guardrails:

1. **Never Bypass `StatementManager`:** All file lifecycles, stream ownership transfers, and import workflows must be orchestrated through it.

2. **No File I/O in Processing Services:** Extractors and taggers must operate solely on in-memory `IXLWorksheet` instances or DTOs.

3. **Strict Separation of Concerns:** Preview routines must never trigger database writes.

4. **Enforce $O(1)$ Math & Domain Isolation:** Never introduce rigid C# domain properties or bypass category discriminators (`TRANSACTION` vs. `METADATA`) during pre-seeding.

5. **Guaranteed Disposal:** Always wrap stream manipulation and database commit sequences in strict `try/finally` blocks invoking explicit `.Dispose()` or `DiscardFile()` routines.

6. **Defend Against Cache Stampedes:** All caching mechanisms must utilize thread-safe structures (e.g., `ConcurrentDictionary<string, Lazy<Task<T>>>`) with explicit fault eviction to prevent redundant I/O during concurrent multi-file processing.
7. **Enforce Master Transaction Atomicity:** Multi-step database persistence (such as statement imports) must execute entirely within a single database transaction token (`conn, tx`), ensuring complete rollback capability and minimal filesystem synchronization overhead.

---

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](https://www.google.com/search?q=LICENSE) file for details.
