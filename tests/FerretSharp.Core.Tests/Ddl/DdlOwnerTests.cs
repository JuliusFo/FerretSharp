using FerretSharp.Core.Ddl;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Ddl;

public class DdlOwnerTests
{
    [Fact]
    public void Table_index_and_foreign_key_into_the_own_schema_lose_the_owner()
    {
        const string ddl = """
            CREATE TABLE "SHOP"."ORDERS"
               (	"ID" NUMBER(10,0) NOT NULL ENABLE,
            	"CUSTOMER_ID" NUMBER(10,0),
            	 CONSTRAINT "ORDERS_PK" PRIMARY KEY ("ID")
              USING INDEX PCTFREE 10 INITRANS 2 TABLESPACE "USERS"  ENABLE,
            	 CONSTRAINT "ORDERS_CUSTOMER_FK" FOREIGN KEY ("CUSTOMER_ID")
            	  REFERENCES "SHOP"."CUSTOMER" ("ID") ENABLE
               ) SEGMENT CREATION IMMEDIATE
              TABLESPACE "USERS"
            """;

        Assert.Equal("""
            CREATE TABLE "ORDERS"
               (	"ID" NUMBER(10,0) NOT NULL ENABLE,
            	"CUSTOMER_ID" NUMBER(10,0),
            	 CONSTRAINT "ORDERS_PK" PRIMARY KEY ("ID")
              USING INDEX PCTFREE 10 INITRANS 2 TABLESPACE "USERS"  ENABLE,
            	 CONSTRAINT "ORDERS_CUSTOMER_FK" FOREIGN KEY ("CUSTOMER_ID")
            	  REFERENCES "CUSTOMER" ("ID") ENABLE
               ) SEGMENT CREATION IMMEDIATE
              TABLESPACE "USERS"
            """, DdlOwner.Omit(ddl, "SHOP"));
    }

    // Other schemas, the owner in another case, a column called like the owner, nothing to omit, the owner's name as the
    // object of another schema.
    [Theory]
    [InlineData("""CONSTRAINT "FK" FOREIGN KEY ("COUNTRY") REFERENCES "MASTER"."COUNTRY" ("CODE") ENABLE""")]
    [InlineData("""CREATE TABLE "shop"."T" ("A" NUMBER)""")]
    [InlineData("""CREATE TABLE "T" ("SHOP" VARCHAR2(10), CHECK ("SHOP" IS NOT NULL))""")]
    [InlineData("")]
    [InlineData("""DEFAULT "OTHER"."SHOP"."NEXTVAL" """)]
    public void Only_the_quoted_owner_before_a_dot_goes(string ddl)
    {
        Assert.Equal(ddl, DdlOwner.Omit(ddl, "SHOP"));
    }

    [Fact]
    public void Only_the_first_part_of_a_dotted_name_is_the_owner()
    {
        // A sequence or function named like its schema (Review): only the schema goes.
        Assert.Equal("""
            CREATE TABLE "T" ("ID" NUMBER DEFAULT "SHOP"."NEXTVAL", "V" AS ("SHOP"."F"("ID")))
            """, DdlOwner.Omit("""
            CREATE TABLE "SHOP"."T" ("ID" NUMBER DEFAULT "SHOP"."SHOP"."NEXTVAL", "V" AS ("SHOP"."SHOP"."F"("ID")))
            """, "SHOP"));
    }

    [Fact]
    public void Literals_and_comments_stay()
    {
        const string ddl = """
            CREATE TABLE "SHOP"."T" ("NOTE" VARCHAR2(30) DEFAULT '"SHOP".x' -- "SHOP".y
            , "Q" VARCHAR2(30) DEFAULT q'["SHOP".z]' /* "SHOP".w */)
            """;

        Assert.Equal("""
            CREATE TABLE "T" ("NOTE" VARCHAR2(30) DEFAULT '"SHOP".x' -- "SHOP".y
            , "Q" VARCHAR2(30) DEFAULT q'["SHOP".z]' /* "SHOP".w */)
            """, DdlOwner.Omit(ddl, "SHOP"));
    }

    [Fact]
    public void View_and_trigger_source_keeps_unquoted_names()
    {
        const string view = """
            CREATE OR REPLACE FORCE EDITIONABLE VIEW "SHOP"."V" ("ID") AS
              SELECT SHOP.ID FROM SHOP.ORDERS SHOP
            """;
        const string trigger = """
            CREATE OR REPLACE EDITIONABLE TRIGGER "SHOP"."ORDERS_BI" BEFORE INSERT ON "SHOP"."ORDERS" FOR EACH ROW
            BEGIN
              :NEW.ID := SHOP.ORDERS_SEQ.NEXTVAL;
            END;
            /
            ALTER TRIGGER "SHOP"."ORDERS_BI" ENABLE
            """;

        Assert.Equal("""
            CREATE OR REPLACE FORCE EDITIONABLE VIEW "V" ("ID") AS
              SELECT SHOP.ID FROM SHOP.ORDERS SHOP
            """, DdlOwner.Omit(view, "SHOP"));
        Assert.Equal("""
            CREATE OR REPLACE EDITIONABLE TRIGGER "ORDERS_BI" BEFORE INSERT ON "ORDERS" FOR EACH ROW
            BEGIN
              :NEW.ID := SHOP.ORDERS_SEQ.NEXTVAL;
            END;
            /
            ALTER TRIGGER "ORDERS_BI" ENABLE
            """, DdlOwner.Omit(trigger, "SHOP"));
    }

    [Fact]
    public void Grants_lose_the_owner_but_keep_the_grantee()
    {
        var grants = GrantDdl.Script(new TableRef("SHOP", "ORDERS"),
            [new ObjectGrant("SHOP", "SELECT", [], "SHOP", false, false)]);

        Assert.Equal("""GRANT SELECT ON "ORDERS" TO "SHOP" """.TrimEnd(), DdlOwner.Omit(Assert.Single(grants), "SHOP"));
    }
}
