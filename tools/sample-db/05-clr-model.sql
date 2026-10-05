-- Columns for the C# sample model (samples/FerretSharp.SampleModel, WP-11): a J/N flag (custom bool converter) and a
-- customer kind stored as a number (enum without converter).
SET FEEDBACK OFF
SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

ALTER TABLE KUNDEN ADD (
    GESPERRT   CHAR(1 BYTE) DEFAULT 'N' NOT NULL CONSTRAINT CK_KUNDEN_GESPERRT CHECK (GESPERRT IN ('J', 'N')),
    KUNDENART  NUMBER(2) DEFAULT 1 NOT NULL);

COMMENT ON COLUMN KUNDEN.GESPERRT IS 'J = gesperrt, N = nicht gesperrt';
COMMENT ON COLUMN KUNDEN.KUNDENART IS '1 = Privat, 2 = Gewerbe, 3 = Behörde';

UPDATE KUNDEN SET GESPERRT = CASE WHEN MOD(KUNDE_ID, 37) = 0 THEN 'J' ELSE 'N' END,
                  KUNDENART = MOD(KUNDE_ID, 3) + 1;
COMMIT;

EXIT
