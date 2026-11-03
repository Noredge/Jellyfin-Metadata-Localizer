# Validation

## Local performance and source cleanup candidate

The automated gate contains 860 checks. Added checks cover empty-name rendering without unrelated rule loads, fresh named rendering after rule edits, inventory scope lifetime and disposal, and execution/write isolation from inventory snapshots. The web checks verify paired error translations, multiline exported function execution, exact source-to-resource assembly and full GET-only page initialization.

Existing concurrency, source-authority, late-result, dictionary, browser-state and recovery regressions remain in the same gate. Performance comparisons use exact before/after assemblies on owned data copies or isolated Jellyfin hosts with synthetic transport; they do not issue real provider calls or change the installed plugin. Changes to local operation timings are separate from actual model latency and browser rendering performance.

## Release candidate: 1.0.0-rc.3

Added regressions exercise verified name slots, ordinary words containing a translated name, identical frozen names, renamed/deleted rules and unchanged historical plain text. Injected HTTP tests verify that both title and synopsis parsers retain usable templates.

Late-result checks hold a real SQLite write lock beyond the busy timeout, verify a durable response file, then recover by storage retry or executor restart. The original unknown evidence remains, no result is automatically displayed, and replaying a durable copy cannot create another translation or provider request. Invalid response files are retained with degraded health. Atomic temporary-file publication cannot protect a response that has not yet reached durable storage before forced process termination or disk failure.

Receipt checks cover preview deletion, restart, changed settings, cancellation, command conflicts and transaction rollback. Native HTTP checks additionally cover expired first submissions, expired committed receipts, legacy binding and actual Jellyfin restart. Older unbound receipts can be verified while their original preview still exists; once an older preview was already deleted, its association cannot be reconstructed safely.

Full-data startup and paired rollback are checked separately from synthetic media tests. The database schema definition remains unchanged; no existing translations or rules are rewritten. Real provider calls and a 24-48 hour soak are not part of this validation.

## Release candidate: 1.0.0-rc.2

The automated gate contains 819 checks. Added executor regressions preserve unknown-outcome consent across restart, prompt/model/person changes and a return to the original target language. Replacement media and shared-term tasks without their own attempt cannot bypass acknowledgement; explicit consent permits one new send while preserving the original evidence. Other fields and languages remain independent. Display-only retries and selected-cache reuse do not require or clear consent; any later generation still checks it at request preparation.

Person-rule tests cover transactional original/alias conflicts, unchanged rejected edits, explicit alias reassignment, legacy ambiguity before sending, cached-render safeguards and ambiguous historical display names. Existing records are retained rather than rewritten. Matching checks cover ordinary Latin words, accented/combining characters, punctuation and CJK adjacency; injected HTTP tests verify the actual title/synopsis payload. The UI error test checks actionable conflict feedback.

Schema 1, its definition hash, the public identity and data directory remain unchanged. A small additive partial index supports the consent lookup. Full-data startup and paired rollback acceptance separately verify preserved rows, local rules, encrypted credentials and host metadata. All provider responses used by validation are synthetic; real-provider calls and a 24-48 hour candidate soak remain outside this gate.

## Release candidate: 1.0.0-rc.1

The automated gate contains 776 checks. It adds correctly hashed archives with missing, empty or unsupported database definitions; backup/restore must reject them without publishing a destination. The supported hash is checked against the runtime schema resource. Path checks cover Windows console aliases, superscript COM/LPT devices, invalid filename characters and valid Unicode names without opening any device.

Injected HTTP checks cover English, Chinese, French and mixed-language source text across title, synopsis and dictionary generation for both output languages, including name protection and response parsing. Changed prompts have new request fingerprints; stale frozen inputs cannot send under a different prompt. These tests validate request construction and parsing, not a real model's translation quality. Existing translation selections and local rule content remain reusable.

Paired backup/restore and startup of full-data copies verify unified preview compatibility independently from fresh initialization and synthetic native-host checks. This is a prerelease for Jellyfin 12.0.0 / Windows x64. Real provider end-to-end calls and a 24-48 hour candidate soak are not part of this release gate; no real translation requests are sent during validation or deployment.

## Unified maintenance: 0.12.0-preview.2

The automated gate covers 700 checks: the prior 623 unified regressions, 24 local-rule checks, 20 offline backup checks, 26 adoption checks and seven public setup checks. Added coverage includes optional local defaults, content fingerprints, frozen process configuration, malformed rules failing before DB writes, UTF-8 BOM input, manual selection preservation, server ownership, unfinished/unknown request guards, uncertain display guards, never-overwrite behavior and committed WAL preservation.

Isolated native tests also cover an invalid local file keeping Jellyfin running with the executor unavailable, correcting/removing rules on restart, language-specific local prompts with public fallback, and no regeneration of existing translations. The synthetic native suite passed 109 checks. Compatible full-data copies are separately used for actual rollback startup and new-package adoption acceptance; credentials are withheld and original videos are replaced with tiny owned fixtures. No real provider requests are used.

Product versions now have one source, `Directory.Build.props`. The same portable five-DLL package supports default installations and local rules. Offline adoption copies an explicitly selected compatible store into a new directory and retains the original; it does not select older data automatically or revive old work. First adoption-fixture failure was a missing input/work association caught by an existing schema trigger; the fixture was corrected without relaxing the constraint.

## Historical preview.1 evidence

Version 0.12.0-preview.1 starts from the unified architecture. Synthetic regression coverage includes durable idempotent work, dependencies, source revisions, external edits, manual control, unknown/late outcomes, rate limits, deadlines, eleven real child-process interruption/recovery boundaries, XML limits/ownership, readonly pooling, search indexes, missing-only queries, response fencing, reading anchors, pagination and polling backoff.

Public-specific checks cover fresh initialization beside an exclusively locked older store, automatic processing off, independent storage, public identity and general prompt resources. Offline backup checks cover integrity, digests, independent restore, active locks, path traversal and never-overwrite behavior. No test needs real provider traffic.

The preview gate contains **650 automated checks**: 623 unified regression checks, 20 offline backup checks and seven public setup checks. A separate isolated Jellyfin 12.0.0 Windows host passed **104 native checks**, including real plugin startup, fresh initialization, missing-only fill, cache reuse, concurrency guards, restart recovery, preserved NFO bytes and database integrity. Its complete plugin directory was backed up and restored with every byte matched. All model responses used an injected synthetic transport.

Actual Jellyfin Web acceptance covered Chinese/English settings at desktop and 360px viewport widths, draft model lookup without preselecting/saving a model, provider order, optional testing, long-text expansion, retained readers and unknown-result confirmation/cancellation. The first setup regression found an unused older initialization message; it was removed and the regression passed again. An initial native harness failure counted its own browser lookup incorrectly; the harness baseline was corrected before all 104 checks passed.

Package inspection also found language prompts being built as satellite resources. They are now embedded in the main plugin assembly. Packaging verifies both logical resource names and exact bytes for both prompts, the UI and genre dictionary; the five-DLL package must pass this gate.

Release evidence records the final commit, exact CI artifact identity, hashes and subsequent artifact startup acceptance. CI evidence is linked from the repository Actions tab and release notes. Third-party models are not individually certified; Linux, Docker, mobile client applications, browser CPU/energy and long-duration large-library load are unvalidated. No automatic old-data migration is claimed.
