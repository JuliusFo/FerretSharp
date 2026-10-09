-- A flags enum for the C# sample model: KUNDEN.MERKMALE holds the sum of Kundenmerkmale (Stammkunde = 1,
-- Newsletter = 2, Lastschrift = 4, Export = 8). Every 101st customer has a bit no member stands for (16).
SET FEEDBACK OFF
SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

ALTER TABLE KUNDEN ADD (MERKMALE NUMBER(3) DEFAULT 0 NOT NULL);

COMMENT ON COLUMN KUNDEN.MERKMALE IS 'Summe: 1 = Stammkunde, 2 = Newsletter, 4 = Lastschrift, 8 = Export';

UPDATE KUNDEN SET MERKMALE = MOD(KUNDE_ID, 16) + CASE WHEN MOD(KUNDE_ID, 101) = 0 THEN 16 ELSE 0 END;
COMMIT;

EXIT
