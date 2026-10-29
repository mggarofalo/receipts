# Partitioned offline journal v2

`PartitionedBatchedPairedStageCampaign` is the additive offline controller for the
frozen C# v2 complete-coverage exporter. `LivePartitionedBatchedPairedStageCampaign`
shares the existing explicitly invoked reserved-send seam; neither is registered
in production or exposed through a CLI. Existing v1 controllers and journals keep
their schema; a v2 binding cannot reopen a v1 journal.

The immutable `verifiers-v2` event binds every path request in order followed by
every contiguous coverage request. Coverage stage names include the absolute batch
index (`global-coverage:4`); path names remain `verifier:0`. The exporter rejects
known initial overflow before exposing requests and exact dynamic overflow before
verifier binding. No alternatives are dropped to satisfy the 30-call limit.

All requests must fit the existing 524288-byte aggregate canonical verifier record
cap and 2MiB journal. An oversized vector fails at binding before any verifier
reservation. Each response passes the actual frozen C# ordered-prefix parser
before ledger success; final success also requires complete aggregation. Reopen
revalidates the durable prefix and, when complete, aggregation. C# retains the
complete taxonomy and uses partition maxima only as conservative logical guards.

Synthetic tests use invented taxonomy and recorded/fake sends only. They require
Windows x64, an x64 .NET 10 SDK and installed x64 .NET 10 runtime, and all
win-x64 runner dependencies already present in the local NuGet global package
cache. Setup restores exclusively from that local cache with NuGet audit fetching
disabled, then performs a framework-dependent win-x64 publish. Missing cached
packages fail setup; no implicit dependency downloads are needed. Every published
file, the complete source snapshot, the installed .NET runtime and (for live tests)
the frozen Python runtime remain in the inventory and all hash gates remain active.
The compact synthetic fixture retains twelve distinct valid definitions exceeding
40KB together, selects one plausible path, and requires at least two complete
coverage partitions. Large 309/432/433-node capacity cases remain separate. Run the partitioned stage and
frozen-live modules together with existing ledger/batched modules for compatibility.
No provider quality, pricing, capacity or production activation claim is implied.
