# 📊 Income & Expenditure Tracker

> A high-performance, resilient desktop personal finance application built with C# (.NET 8), Avalonia UI, SQLite, Dapper, and ClosedXML.

---

## 🚀 Overview

The **Income & Expenditure Tracker** is a precision financial calculation and data presentation tool designed to ingest messy, multi-year bank statements, automatically detect spreadsheet layouts, and present clean, structured transaction data for human interpretation.

Unlike bloated financial advisory apps that rely on subjective heuristics, this application focuses strictly on **data accuracy, high-speed extraction, and zero-leak OS resource management**. It features a robust, self-learning backend architecture that adapts to bank formatting anomalies without ever freezing the UI or locking physical spreadsheet files. The system enforces a **"Guilty Until Proven Innocent"** audit trail, ensuring that ambiguous or zero-value financial rows are gracefully mapped to `0m` and explicitly flagged for user review to prevent silent data loss.

---

## ✨ Key Features

- **⚡ Concurrent Lock-Free Ingestion:** Stage up to 5 multi-year Excel workbooks simultaneously using non-blocking asynchronous workflows (`Task.WhenAll` and `ConcurrentDictionary`).

- **🛡️ Ironclad OS Resource Management:** Strictly enforced `IDisposable` patterns and atomic `DiscardFile` routines guarantee Windows OS file locks are released immediately upon stream transfer, error trapping, or cancellation.

- **🧠 User-Confirmed Symmetrical Self-Learning:** Uses atomic services to dynamically learn new bank headers, category mappings, and merchant keyword rules. Learning triggers **only** after explicit user verification via a decoupled, fire-and-forget background "Ripple Effect" mechanism.

- **🛡️ Concurrency & Stampede Defense:** All reference and lookup services utilize an async lazy cache registry (`ConcurrentDictionary<string, Lazy<Task<T>>>`) with automatic fault eviction.

- **🔒 Master Transaction Atomicity & Zero-Lock Math:** Batch persistence executes under a single master database transaction token (`conn, tx`), holding all entity creation, batch auditing, and bulk transaction insertion in the WAL buffer for a single disk synchronization and 100% all-or-nothing rollback protection.

- **⚡ Race-Condition Free Upserts & Stateless WAL Queries:** Source, account, and tagging services execute atomic SQLite upserts, while dashboard queries hit native C-compiled B-tree indexes for sub-2-millisecond retrievals.

- **🎯 Deterministic Tagging & Ambiguity Guardrails:** Evaluates multi-keyword merchant descriptions using a 3-tier matrix.

- **🏎️ Zero-Allocation Tokenization & Thread-Local Memory:** `DescriptionParser` replaces regex with zero-allocation character math, truncates descriptions to 255 characters, and explicitly strips out suspicious HTML/Script tags to prevent SQL overflow and XSS vulnerabilities.

- **📐 $O(1)$ Coordinate-Driven Math:** High-volume transaction loops operate strictly on boundary-resolved integer coordinates (`TransactionColumnCoordinates`).

- **🌍 File-Agnostic Parsing & International Guardrails:** Core financial parsing (`IStrictAccountParser`) is decoupled from ClosedXML, relying on a high-performance decimal "Fast-Path" and a rigorous 5-layer regex "Firewall" to handle garbage text, invalid currency strings, and spreadsheet errors. Invalid dates (e.g., "Feb 30th 2026") fall back to `default(DateTime)` and are explicitly flagged for human review.

- **🏛️ Strict UI Facades & Master Orchestration:** Post-persistence UI interaction is exclusively mediated by central orchestrators (`MasterDataOrchestrator`, `TransactionReviewOrchestrator`).

- **🌳 Taxonomy Safe Re-parenting & Hard Blocks:** Enforces a "Tag is King" model. Deleting parent nodes triggers safe re-parenting to floating tags or a system "Misc Tag" (ID 999), and strict structural checks throw `InvalidOperationException` to prohibit the deletion of ledgers containing historical data.

- **🚨 Two-Tiered Error Strategy & Aggregation:** Centralizes error routing into `StatementManager`. Data-level anomalies map to UI review flags (Tier 1), while app-level infrastructure faults are trapped and routed via `FileStagingError` payloads based on severity. The `ParseErrorMessage` engine concatenates failures into clear feedback for the data grid.

---

## 🛠️ Tech Stack & Dependencies

| Component / Library       | Technology                               | Version            | Purpose                                                      |
| ------------------------- | ---------------------------------------- | ------------------ | ------------------------------------------------------------ |
| **Framework**             | .NET (C#)                                | `8.0.x`            | Core application runtime and backend logic.                  |
| **Presentation Layer**    | Avalonia UI                              | `[Insert Version]` | Cross-platform MVVM desktop UI rendering.                    |
| **Database Engine**       | SQLite                                   | `[Insert Version]` | Local relational persistence with WAL journal mode.          |
| **ORM / Data Access**     | Dapper                                   | `[Insert Version]` | High-performance micro-ORM for coordinate-optimized queries. |
| **Excel Spreadsheet I/O** | ClosedXML                                | `[Insert Version]` | Stream-based workbook loading and worksheet manipulation.    |
| **Dependency Injection**  | Microsoft.Extensions.DependencyInjection | `[Insert Version]` | Interface-driven service lifecycle management.               |
| **Structured Logging**    | Microsoft.Extensions.Logging             | `[Insert Version]` | Asynchronous lifecycle, debug, and error telemetry.          |

---

## 🏗️ Architecture & Core Principles

- **Orchestration Ownership (`StatementManager`):** The manager strictly coordinates workflows, thread-safe staging queues, and workbook stream lifecycles. It acts as the universal error sink.

- **In-Memory & Read-Only Analysis:** Extractors, parsers, and preview analysis routines operate solely on in-memory `IXLWorksheet` instances and DTOs.

- **Resilient SQLite Access:** Every database query is routed through a centralized `DatabaseService.ExecuteWithRetryAsync()` wrapper to safely handle `SQLITE_BUSY` contentions natively.

- **Open-Closed Schema Flexibility:** All data transfer across extraction boundaries relies on namespaced, case-insensitive dictionaries (`Dictionary<string, DetectedField>`).

- **Concurrency & Stampede Defense:** All reference and lookup services implement thread-safe lazy caching with automatic fault eviction.

- **Domain Isolation:** Header detection and dictionary pre-seeding strictly segregate domain concepts (e.g., transaction columns vs. account metadata fields) to prevent cross-contamination.

- **Master Transaction Boundaries & Single Disk Sync:** Batch persistence routines hold all operations under a single database transaction token to guarantee atomicity and minimize filesystem disk synchronization overhead.

- **Strict UI Facades (Post-Persistence):** The MVVM frontend strictly interacts with centralized orchestrators, allowing errors to bubble up directly to Avalonia ViewModels for user broadcast.

- **Secure UI Binding Contract:** Visual controls passing passwords bypass managed string heap retention by passing references directly to commands, enabling instantaneous memory eradication (`passwordBox.Text = string.Empty`).
- **Dynamic View Routing:** The application utilizes a single-page `<ContentControl>` router reacting to `NavigationMessage` payloads emitted through the `IApplicationBroker` to shift between the authentication Gatekeeper and the main Dashboard.
- **Global Design System:** All Avalonia visual definitions (typography, themes, behaviors) reside centrally in `App.axaml`, acting as a global CSS controller.

---

## 📂 Solution Structure

```plaintext
IncomeExpenseTracker/
├── .gitignore
├── IncomeExpenditureTracker.Tests/
│   ├── Architecture/
│   │   └── Tools/
│   │       ├── DatabaseEncapsulationTests.cs
│   │       ├── IArchitectureValidator.cs
│   │       └── NetArchTestValidator.cs
│   ├── Fixtures/
│   │   ├── DatabaseTestFixture.cs
│   │   └── ExcelStatementGenerator.cs
│   ├── Helpers/
│   │   └── SeedSynonymTable.cs
│   ├── IncomeExpenditureTracker.Tests.csproj
│   ├── Integration/
│   │   ├── DatabaseServiceAirlockTests.cs
│   │   ├── HeadlessWorkflowIntegrationTests.cs
│   │   ├── MasterDataOrchestratorTests.cs
│   │   ├── StatementManagerTests.cs
│   │   ├── TagServiceConcurrencyTests.cs
│   │   ├── TransactionManagementTests.cs
│   │   └── TransactionReviewOrchestratorTests.cs
│   ├── Logic/
│   │   ├── ConfidenceServiceTest.cs
│   │   ├── DescriptionParserTest.cs
│   │   ├── ExcelStatemenetImportTests.cs
│   │   ├── ExcelTransactionExtractorTest.cs
│   │   ├── FieldMapperTest.cs
│   │   ├── HeaderDetectorTest.cs
│   │   ├── Profiles/
│   │   │   ├── PasswordHasherTests.cs
│   │   │   ├── ProfileCryptographyTests.cs
│   │   │   ├── ProfileLoginServiceTests.cs
│   │   │   └── ProfileRegistryTests.cs
│   │   ├── StatementEditSessionTests.cs
│   │   └── TagEngineTest.cs
│   ├── Observability/
│   │   ├── StatementErrorSink.cs
│   │   └── TestOutputLoggerProvider.cs
│   └── UI/
│       └── ViewModels/
│           ├── GateKeeper/
│           │   ├── LoginViewModelTests.cs
│           │   └── RegisterViewModelTests.cs
│           ├── Ledger/
│           │   └── TransactionReviewViewModelTests.cs
│           ├── MasterData/
│           │   └── MasterDataViewModelTests.cs
│           └── Shell/
│               └── MainWindowViewModelTests.cs
├── IncomeExpenditureTracker/
│   ├── App.axaml
│   ├── App.axaml.cs
│   ├── Assets/
│   │   └── avalonia-logo.ico
│   ├── IncomeExpenditureTracker.csproj
│   ├── Models/
│   │   ├── Diagnostics/
│   │   │   ├── ErrorSeverity.cs
│   │   │   └── FileStagingError.cs
│   │   ├── Entities/
│   │   │   ├── Account.cs
│   │   │   ├── Category.cs
│   │   │   ├── Entity.cs
│   │   │   ├── ImportBatch.cs
│   │   │   ├── ProfileDto.cs
│   │   │   ├── SubCategory.cs
│   │   │   ├── Synonym.cs
│   │   │   ├── Tag.cs
│   │   │   ├── TagRule.cs
│   │   │   └── Transaction.cs
│   │   ├── Import/
│   │   │   ├── DetectedField.cs
│   │   │   ├── SheetMetaData.cs
│   │   │   └── SynonymFieldType.cs
│   │   ├── PreviewInsights/
│   │   │   ├── PreviewTracker.cs
│   │   │   ├── StatementPreview.cs
│   │   │   └── TransactionPreview.cs
│   │   └── utilities/
│   │       ├── AmountParserResult.cs
│   │       ├── AppMessages.cs
│   │       ├── FieldScoringRules.cs
│   │       ├── LoadingProgress.cs
│   │       ├── StagedFileTracker.cs
│   │       ├── SystemConstants.cs
│   │       ├── TagEngineDTOs.cs
│   │       ├── ToastAlert.cs
│   │       └── TransactionReviewDTOs.cs
│   ├── Program.cs
│   ├── Services/
│   │   ├── Database/
│   │   │   ├── DatabaseInitializer.cs
│   │   │   ├── DatabaseService.cs
│   │   │   ├── IDatabaseInitializer.cs
│   │   │   ├── IDatabaseService.cs
│   │   │   └── Profiles/
│   │   │       ├── IPasswordHasher.cs
│   │   │       ├── IProfileCryptography.cs
│   │   │       ├── IProfileLoginService.cs
│   │   │       ├── IProfileRegistry.cs
│   │   │       ├── PasswordHasher.cs
│   │   │       ├── ProfileCryptography.cs
│   │   │       ├── ProfileLoginService.cs
│   │   │       └── ProfileRegistry.cs
│   │   ├── DependencyInjections/
│   │   │   └── ServiceRegistration.cs
│   │   ├── Entities/
│   │   │   ├── AccountService.cs
│   │   │   ├── CategoryService.cs
│   │   │   ├── EntityService.cs
│   │   │   ├── IAccountService.cs
│   │   │   ├── ICategoryService.cs
│   │   │   ├── IEntityService.cs
│   │   │   ├── IImportBatchService.cs
│   │   │   ├── ISubCategoryService.cs
│   │   │   ├── ISynonymService.cs
│   │   │   ├── ITagService.cs
│   │   │   ├── ITransactionService.cs
│   │   │   ├── ImportBatchService.cs
│   │   │   ├── SubCategoryService.cs
│   │   │   ├── SynonymService.cs
│   │   │   ├── TagService.cs
│   │   │   └── TransactionService.cs
│   │   ├── Helpers/
│   │   │   ├── DescriptionParser.cs
│   │   │   ├── FieldMapper.cs
│   │   │   ├── HeaderDetector.cs
│   │   │   ├── IDescriptionParser.cs
│   │   │   ├── IFieldMapper.cs
│   │   │   ├── IHeaderDetector.cs
│   │   │   ├── IStrictAmountParser.cs
│   │   │   └── StrictAmountParser.cs
│   │   ├── Importing/
│   │   │   ├── ExcelStatementExtractor.cs
│   │   │   ├── ExcelStatementImport.cs
│   │   │   ├── IStatementExtractor.cs
│   │   │   └── IStatementImport.cs
│   │   ├── Messaging/
│   │   │   ├── IApplicationBroker.cs
│   │   │   └── ToolkitMessengerAdapter.cs
│   │   ├── Orchestration/
│   │   │   ├── IMasterDataOrchestrator.cs
│   │   │   ├── ITransactionReviewOrchestrator.cs
│   │   │   ├── MasterDataOrchestrator.cs
│   │   │   └── TransactionReviewOrchestrator.cs
│   │   ├── PreviewInsights/
│   │   │   ├── ConfidenceService.cs
│   │   │   └── IConfidenceService.cs
│   │   ├── StatementManagement/
│   │   │   ├── IStatementEditSession.cs
│   │   │   ├── IStatementLoader.cs
│   │   │   ├── StatementEditService.cs
│   │   │   ├── StatementLoader.cs
│   │   │   └── StatementManager.cs
│   │   ├── Tagging/
│   │   │   ├── ITagEngine.cs
│   │   │   └── TagEngine.cs
│   │   └── TransactionExtractor/
│   │       ├── ExcelTransactionExtractor.cs
│   │       └── ITransactionExtractor.cs
│   ├── UI/
│   │   ├── GateKeeper/
│   │   │   ├── ViewModels/
│   │   │   │   ├── LoginViewModel.cs
│   │   │   │   └── RegisterViewModel.cs
│   │   │   └── Views/
│   │   │       ├── LoginView.axaml
│   │   │       ├── LoginView.axaml.cs
│   │   │       ├── RegisterView.axaml
│   │   │       └── RegisterView.axaml.cs
│   │   ├── ImportHub/
│   │   │   └── StatementEditViewModel.cs
│   │   ├── Ledger/
│   │   │   └── TransactionReviewViewModel.cs
│   │   ├── MasterData/
│   │   │   └── MasterDataViewModel.cs
│   │   ├── Shared/
│   │   │   └── ViewModelBase.cs
│   │   └── Shell/
│   │       ├── ViewModels/
│   │       │   └── MainWindowViewModel.cs
│   │       └── Views/
│   │           ├── MainWindow.axaml.cs
│   │           └── MainWindowView.axaml
│   ├── ViewLocator.cs
│   └── app.manifest
├── Income_Expenditure_tracker.sln
├── LICENSE
└── README.md

```

---

## ⚡ High-Level Processing Pipeline

```plaintext
[Avalonia UI: Gatekeeper] ──► Startup Profile Selector (AppProfile_{Guid}.db)
   ▼
[ProfileLoginService] ──► Validates via PBKDF2 & Instantly Eradicates Memory
   ▼
[MainWindow Router] ──► Dynamic View Swap (ContentControl)
   ▼
[StatementManager] ──► Orchestrates concurrent staging via ConcurrentDictionary
   │
   ├─► [ExcelStatementExtractor] ──► Utilizes FieldMapper & HeaderDetector to generate previews
   │                               ├─► [ExcelTransactionExtractor] (O(1) Coordinate Math & Validation)
   │                               └─► [ConfidenceService] (Trustworthiness Scoring)
   ▼
[StatementEditSession] (Users apply mapping corrections via UI dropdowns)
   │
   ▼
[ExcelStatementImportService.ImportConfirmedStatementAsync]
   ├─► [PHASE 1: 100% In-Memory Math — Zero DB Write Locks Held]
   │
   ├─► [PHASE 2: Master ExecuteInTransaction Block — Pure Sequential I/O]
   │        └─► Holds Account upserts, Batch Audits, and Dapper Bulk Inserts under one atomic token
   │
   ├─► [PHASE 3: Non-Blocking Background Dispatch]
   │        └─► [The "Ripple Effect"] (Tag/Synonym rule learning via decoupled Task.Run)
   │
   └─► [finally Clause] ────────────────────► [Dispose Stream -> Instant OS Lock Release]

[Post-Persistence UI]
   ▼
[TransactionReviewOrchestrator] ──► Avalonia Views request filtered DTOs and execute batch corrections

```

---

## 🗺️ Roadmap & Development Progress

### ✅ Completed Milestones

- [x] Modular service structure and interface-driven Dependency Injection registry.

- [x] Centralized retry-based SQLite access with WAL mode and Foreign Key enforcement.

- [x] Boundary coordinate resolution via `TransactionColumnCoordinates` for $O(1)$ parsing.

- [x] Thread-safe concurrent multi-file staging and ironclad OS lock release.

- [x] Master import orchestration under a single database transaction with 100% all-or-nothing atomicity.

- [x] Phase 3 Complete: Zero-Allocation Tagging Ecosystem & Master Import Integration.

- [x] Phase 4 Complete: Integration, Concurrency & Orchestration Testing. Validated stampede defense with 50 concurrent threads. Transaction Management natively handles `SQLITE_BUSY` contentions, validated using physical temporary `.db` files and explicit `Rollback()` testing.

- [x] Phase 4.5 Complete: Master Data Orchestration. Established `MasterDataOrchestrator` for taxonomy tree management, implementing floating tags and the `Misc Tag` (ID 999) fallback. Established `TransactionReviewOrchestrator` for high-speed Dapper bulk `UPDATE` statements and decoupled "Ripple Effect" background rule learning.

- [x] Phase 4 Finalization: Headless Testing & Extractor Edge Cases. Fully validated 100% atomic master rollbacks. Fixed the `IsValidRow` audit trail to explicitly pass rows with `NeedsReview == true`. Implemented file-agnostic parsing via `IStrictAccountParser`, and integrated HTML/XSS description sanitization alongside `ParseErrorMessage` string aggregation.

- [x] Phase 5 Complete: Reactive UI Messenger & MVVM Refactor. Established `IApplicationBroker` for cross-thread domain messaging, utilizing `AppMessages.cs` record envelopes. Zero-leak `ViewModelBase` enforces safe teardown logic via `IDisposable`.
- [x] Phase 6 Complete: Secure User Profiles & Data Isolation. Integrated SQLCipher via PRAGMA key logic and engineered a thread-safe Database Airlock for seamless SQLite profile swapping (`ProfileCryptography.cs`).
- [x] Phase 7 (Gatekeeper & Routing) Complete: Engineered the `LoginView` and `RegisterView` utilizing Secure UI Binding. Configured `MainWindow.axaml` and `MainWindowViewModel` to act as a 2-column dynamic `<ContentControl>` router reacting to `NavigationMessage` payloads.

### 🎯 Immediate Focus (Phase 7: Functional UI Integration)

- [ ] **Phase 7: Core Presentation Layer (Data Grids):** Build out the Import Hub (`StatementEditViewModel`) with drag-and-drop ingestion support, and implement the Hybrid Ledger (`TransactionReviewViewModel`) virtualized B-tree transaction grids utilizing raw Avalonia controls to validate end-to-end data pipelines prior to styling.

### 🔮 Future Roadmap

- [ ] **Phase 8: Analytics & Visualization:** Write aggregation queries and integrate a charting library (like LiveCharts2) for spending trends and visual summaries.

- [ ] **Phase 9: Management & Edit Services:** Build the "Shopping Cart" UI for the `StatementEditSession` pre-import verification grid and simple CRUD management screens utilizing the `MasterDataOrchestrator`.

- [ ] **Phase 10: Global Design System:** Transition crude UI data grids into a cohesive style architecture, utilizing `App.axaml` as a global CSS controller for typography, Indigo color accents, and seamless Light/Dark mode transitions.

---

## 🤝 Contributing & AI Guardrails

When contributing or utilizing AI assistants for further development on this repository, strictly adhere to the project's architectural guardrails:

1. **Never Bypass Orchestrators:** All Avalonia view models must interact via `MasterDataOrchestrator` or `TransactionReviewOrchestrator`. All file lifecycles must route through `StatementManager`.

2. **No File I/O in Processing Services & Testing:** Extractors and parsers must operate solely on in-memory `IXLWorksheet` instances or DTOs. Testing must mock or dynamically generate files via `ExcelStatementGenerator` rather than relying on static binaries.

3. **Strict Separation of Concerns:** Preview generation and analysis routines must never execute database writes.

4. **Do Not Mask Orchestrator Errors (Bubble-Up):** Lower-level transient services must not swallow infrastructure exceptions. Catastrophic faults must naturally bubble up to the `StatementManager` for structured trapping and teardown.

5. **Centralized Database Routing:** Every SQLite query must be wrapped in `DatabaseService.ExecuteWithRetryAsync()`.

6. **Learn Only on Confirmation:** Automatic database updates for synonyms or tag rules must only fire after explicit user approval upon completing the edit session, and should execute asynchronously on background threads.

7. **Enforce Open-Closed Schema Flexibility:** Route all extraction data through case-insensitive dictionary schemas (`Dictionary<string, DetectedField>`). Never re-introduce rigid C# properties or hardcoded switch statements for domain fields.

8. **Maintain Coordinate-Driven Math:** Ensure all high-volume row extraction loops operate strictly on integer coordinates resolved at the method boundary.

9. **Enforce Domain Isolation:** Never allow header detection or dictionary pre-seeding to run without explicit `FieldCategory` scoping (`TRANSACTION` vs. `METADATA`).

10. **Guarantee OS Lock Release:** Always wrap stream manipulation and database commit sequences in strict `try/finally` blocks invoking explicit disposal methods (`DiscardFile`).

11. **Defend Against Cache Stampedes:** All caching mechanisms must utilize thread-safe structures with explicit fault eviction to prevent redundant I/O during concurrent multi-file processing.

12. **Enforce Master Transaction Atomicity:** Multi-step database persistence must execute entirely within a single database transaction token (`conn, tx`), ensuring complete rollback capability and minimal filesystem synchronization overhead.

13. **Strict UI Memory Lifecycles:** Never capture raw authentication string inputs; bind visual inputs using the Secure UI Binding Contract to `PasswordBox` references to permit instantaneous null-terminator application upon command completion.
