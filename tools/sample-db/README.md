# Sample database

A local Oracle 23 Free in Docker with a schema for trying out FerretSharp by hand (the integration tests create
their own containers and do not need this).

```powershell
powershell -ExecutionPolicy Bypass -File tools/sample-db/New-SampleDb.ps1
```

Creates the container `ferret-sample` on `127.0.0.1:1522` (service `FREEPDB1`, user `FERRET`), loads the scripts and
prints the password. The container restarts with Docker, so the port and a saved connection stay valid. `-Force`
recreates it (all data is lost), `-Port`, `-Name` and `-Password` override the defaults.

| Script | Content |
|---|---|
| `01-schema.sql` | KUNDEN, AUFTRAG, positions and deliveries (composite FK), RECHNUNG, quoted identifiers (`"MixedCase"`, `"notizen"`), an IOT, views, a materialized view, ~30 master data tables, a dropped table in the recycle bin |
| `02-data.sql` | 150,000 customers (umlauts, `&`, `%`, `_`, NULLs), 50,000 orders with some CLOBs |
| `03-vertrag.sql` | VERTRAG: 71 columns, 20,000 rows, several FKs |
| `04-object-details.sql` | comments, check constraints (one disabled), indexes (function-based, descending), a view on VERTRAG, an invalid view, optimizer statistics |

`04-object-details.sql` can also be added to an existing sample database:

```powershell
docker cp tools/sample-db/04-object-details.sql ferret-sample:/tmp/
docker exec ferret-sample bash -c "NLS_LANG=GERMAN_GERMANY.AL32UTF8 sqlplus -s ferret/<password>@FREEPDB1 @/tmp/04-object-details.sql"
```

Remove it with `docker rm -f ferret-sample`.
