-- A relationship only the C# sample model knows (WP-12): AUFTRAG.BEARBEITER_ID → MITARBEITER without a FK constraint,
-- as in projects that maintain their schema by hand. FerretSharp offers it in the FK navigation, marked "aus C#-Modell".
SET FEEDBACK OFF
SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

-- 03-vertrag.sql may have filled MITARBEITER already: add only the IDs the orders refer to.
INSERT INTO MITARBEITER (ID, BEZEICHNUNG)
SELECT n, 'Mitarbeiter ' || n FROM (SELECT LEVEL AS n FROM DUAL CONNECT BY LEVEL <= 25) WHERE n NOT IN (SELECT ID FROM MITARBEITER);

ALTER TABLE AUFTRAG ADD (BEARBEITER_ID NUMBER(10));
COMMENT ON COLUMN AUFTRAG.BEARBEITER_ID IS 'Bearbeiter (MITARBEITER.ID) – bewusst ohne FK-Constraint';

UPDATE AUFTRAG SET BEARBEITER_ID = CASE WHEN MOD(AUFTRAG_ID, 10) = 0 THEN NULL ELSE MOD(AUFTRAG_ID, 25) + 1 END;
COMMIT;

EXIT
