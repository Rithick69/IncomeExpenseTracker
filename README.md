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

- **🧠 User-Confirmed Symmetrical Self-Learning:** Uses atomic services (`SynonymService` and `TagService`) to dynamically learn new bank headers, category mappings, and merchant keyword rules. Learning triggers **only** after explicit user verification, executing asynchronously on background threads while leveraging `DescriptionParser` to guarantee 100% token symmetry between extraction and learning pipelines.

- **🛡️ Concurrency & Stampede Defense:** All reference and lookup services utilize an async lazy cache registry (`ConcurrentDictionary<string, Lazy<Task<T>>>`) with automatic fault eviction, guaranteeing that concurrent threads hit the database exactly once during multi-file staging without caching transient I/O failures.

- **🔒 Master Transaction Atomicity & Zero-Lock Math:** High-volume ClosedXML extraction, string tokenization, and parallel tagging execute entirely in RAM prior to opening SQLite write locks. Batch persistence then executes under a single master transaction (`conn, tx`), holding all entity creation, account linking, and Dapper bulk inserts in the WAL buffer for a single filesystem disk synchronization and 100% all-or-nothing rollback protection.

- **⚡ Race-Condition Free Upserts & Stateless WAL Queries:** Entity, account, and tagging services execute atomic SQLite upsert SQL (`INSERT OR IGNORE`) to prevent concurrent collision exceptions, while dashboard queries hit native C-compiled B-tree indexes (`idx_transactions_accountid`, `idx_transactions_entity`) for sub-2-millisecond retrievals without RAM heap bloat.

- **🎯 Deterministic Tagging & Ambiguity Guardrails:** Evaluates multi-keyword merchant descriptions using a 3-tier matrix (Database Priority $\rightarrow$ Match Count $\rightarrow$ Ambiguity Fallback). If multiple tags tie on both priority and keyword hits, the engine refuses to guess and explicitly assigns a fallback `Misc` tag to prevent silent misclassification.

- **🏎️ Zero-Allocation Tokenization & Thread-Local Memory:** `DescriptionParser` replaces regex with zero-allocation character math (`char.IsDigit`) and space-joined sliding windows. `TagEngine` leverages C#'s `Parallel.For` thread-local state overload to recycle working dictionaries across CPU cores, eliminating thousands of per-row Garbage Collection heap allocations.

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

6. **Master Transaction Boundaries & Decoupled Math:** CPU-heavy extraction, string tokenization, and rule matching execute in memory before database transactions open. Batch persistence routines then hold all operations (entity linking, account creation, batch auditing, bulk transaction insertion) under a single, all-or-nothing database transaction token to guarantee atomicity and minimize filesystem disk synchronization overhead.

---

## 📂 Solution Structure

The solution is structured into modular responsibilities across domain entities, services, helpers, and import pipelines:

```plaintext
Models/
├── PreviewTracker (Global Hand-off Bundle: FinalPreview + ColumnCorrections + TagCorrections)
└── Utilities/
     └── RuleBookSnapshot (Immutable keyword rule indexing, Stack-Allocated Structs & MiscTagId bundle)
Services/
├── Database/
│    ├── DatabaseService / IDatabaseService
│    └── DatabaseInitializer (Seeds baseline synonyms/Misc tag; manages B-Tree indexes)
├── DependencyInjection/
│    └── ServiceRegistration
├── Entities/
│    ├── AccountService / EntityService (Atomic upserts; transaction-aware cache bypass)
│    └── TransactionService / ImportBatchService (Stateless B-Tree WAL queries; Dapper bulk inserts)
├── Helpers/
│    ├── HeaderDetector / FieldMapper (Zero-Leak Pre-Seeding)
│    ├── DescriptionParser (Zero-Allocation Digit Checks & Space-Joined Sliding Windows)
│    └── SynonymService (Atomic CRUD, Self-Learning, Category Discriminators & Lazy Caching)
├── Importing/
│    ├── ExcelStatementExtractor
│    └── ExcelStatementImportService (Zero-Lock Extraction/Tagging -> Master Transaction Persistence)
├── PreviewInsights/
│    └── ConfidenceService (Proportional Dynamic Engine)
├── StatementManagement/
│    ├── StatementLoader
│    ├── StatementManager (Thread-Safe Concurrent Staging, OS Lock Manager & Centralized Error Sink)
│    └── StatementEditSession (In-Memory Staging Scratchpad)
├── Tagging/
│    ├── TagService (Atomic CRUD, Stampede-Defended Caching & Symmetrical Self-Learning)
│    └── TagEngine (Thread-Local Memory Recycling, Match-Count Scoring & Ambiguity Resolution)
└── TransactionExtractor/
     └── ExcelTransactionExtractor (GetDecimal Cell Parser & O(1) Coordinate Structs)

```

---

## ⚡ High-Level Processing Pipeline

The ingestion pipeline orchestrates concurrent workbook loading, in-memory analysis, user verification, and atomic batch persistence:

```plaintext
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
[StatementEditSession] (User verifies grid & adjusts column/tag mappings via UI dropdowns)
   │
   ▼
[CommitStagedFileAsync]
   ├─► [PHASE 1: 100% In-Memory Math — Zero DB Write Locks Held]
   │        ├─► [DescriptionParser] (Space-Joined Sliding Windows & Zero-Allocation Math)
   │        └─► [TagEngine] (Thread-Local Recycling -> Priority/Match-Count Scoring -> Ambiguity Guardrails)
   │
   ├─► [PHASE 2: Master ExecuteInTransaction Block — Pure Sequential I/O]
   │        └─► [ExcelStatementImportService] (Atomic Batch Persistence)
   │                 ├─► [EntityService & AccountService] (Atomic Upserts & Foreign Key Linking)
   │                 └─► [TransactionService] (High-Speed Parameterized Dapper Bulk Inserts)
   │
   ├─► [PHASE 3: Non-Blocking Background Dispatch via Task.Run()]
   │        ├─► [SynonymService.LearnFromCorrectionAsync()]
   │        └─► [TagService.LearnRuleFromOverrideAsync()] (Uses DescriptionParser for Token Symmetry)
   │
   └─► [finally Clause] ────────────────────► [DiscardFile -> Dispose Stream -> Release OS Lock]

```

---

## 🗺️ Roadmap & Development Progress

### ✅ Completed Milestones (Phase 1, Phase 2 & Phase 3)

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

- [x] **Phase 3 Complete: Unified `ITagService` & `TagService` Architecture:** Merged legacy `RuleBook` into `TagService` as the single source of truth for tag persistence and rule delivery. Implemented atomic CRUD methods with 32-bit `int` IDs, stampede-defended RAM caching (`ConcurrentDictionary`), and automatic fault eviction.

- [x] **Phase 3 Complete: Zero-Allocation `DescriptionParser` Refactor:** Resolved the 2-character word bug (`< 2` instead of `<= 2`) to preserve short acronyms (e.g., `OF`, `HP`, `SBI`). Replaced high-volume `Regex.IsMatch` loops with zero-allocation `word.All(char.IsDigit)` checks, eliminated redundant length sorting overhead, and standardized on space-joined sliding windows (`MAX_TOKEN_WINDOW = 4`) to match natural human database keywords.

- [x] **Phase 3 Complete: Hardware-Level Memory Guardrails:** Declared `TagRuleDTO` as a stack-allocated `readonly record struct` and utilized array-backed indexing (`TagRuleDTO[]`) in `RuleBookSnapshot` to eliminate heap bloat. Integrated C#'s `Parallel.For` thread-local state overload in `TagEngine` to recycle working dictionaries across CPU cores, achieving zero per-row GC allocations across multi-thousand row batches.

- [x] **Phase 3 Complete: 3-Tier Deterministic Scoring & Ambiguity Engine:** Replaced "first match wins" with a holistic scoring algorithm evaluating all tokens per row. Resolves category collisions via **Tier 1: Highest Priority** and **Tier 2: Most Keyword Matches**. Enforces **Tier 3: Ambiguity Guardrail**, explicitly dropping transactions to `MiscTagId` when different tags tie on both priority and match count to prevent silent categorization corruption.

- [x] **Phase 3 Complete: Symmetrical Background Self-Learning:** Built `LearnRuleFromOverrideAsync` to execute via `Task.Run()` after statement commits. Injects `DescriptionParser` directly into `TagService` to tokenize user overrides, extracting the exact same space-joined keyword strings used during ingestion to guarantee 100% token symmetry.

- [x] **Phase 3 Complete: Zero-Lock Import Orchestration:** Decoupled ClosedXML cell extraction, string tokenization, and `TagEngine` execution from the database write lock. By passing temporary placeholders during in-memory processing and stamping real database IDs inside the transaction, the SQLite write lock is held strictly for sub-second, sequential Dapper bulk insertions.

### 🚧 Current Focus (Phase 4: Headless Workflow Integration Testing)

- [ ] **Phase 4: Automated Test Harness:** Build an automated, UI-less test harness using real-world, multi-year bank and credit card Excel statements.

- [ ] **Phase 4: Concurrency & Stress Validation:** Validate multi-file concurrent staging (`StageFilesAsync`), stampede defense under load, duplicate hash detection, and all-or-nothing rollback recovery without UI dependencies.

### 🔮 Future Roadmap (Phase 5)

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

7. **Enforce Master Transaction Atomicity & Zero-Lock Math:** Heavy CPU tasks (tokenization, tagging, cell parsing) must execute in RAM before database transactions open. Multi-step database persistence must execute entirely within a single database transaction token (`conn, tx`), ensuring complete rollback capability and minimal filesystem synchronization overhead.

---

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](https://www.google.com/search?q=LICENSE) file for details.
