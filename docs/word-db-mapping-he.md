# השוואת בסיס הנתונים לאפיון Word

מקור: מנוע חוקה - POC - 18-10-2026.docx. ההשוואה היא ברמת מבנה ואחריות, ולא אישור התאמה של כל שדה ואילוץ.

| באפיון | במימוש | התאמה |
|---|---|---|
| APP_Model | ModelVersions | גרסאות ותוקפים; מבנה שדות שונה |
| TAB_Component | ComponentVersions | מזהה עסקי וגרסה, תוקפים וסוג; מבנה שונה |
| APP_ModelLayerComponent | ModelComponent | שכבות וסדר; הפניה לרכיב לפי מזהה עסקי |
| TAB_PriceListMaintenanceItem, TAB_PriceListHuman | PriceVersions | שני מחירונים אוחדו; נתוני דוגמה מצומצמים |
| APP_MaintenanceItemComponent, APP_HumanComponent | ComponentItem | מפרטים אוחדו תחת גרסת רכיב; באפיון מצביעים לשיוך רכיב למודל |
| APP_DecisionTableRow, APP_DecisionTableCondition | DecisionRow, DecisionCondition | תמיכה בחלק מהמאפיינים והאופרטורים; שיוך לגרסת רכיב |
| TAB_Agreement | AgreementVersions | רק הסכמי רווח למסגרת בדוגמה, לא כל שדות האפיון |
| TAB_TaxType, TAB_Tax | TaxRateVersions | ייצוג מאוחד ומצומצם; לא מיפוי מלא |
| TAB_ComponentType, T_Layer, T_TaxCalc | enum וכללים בקוד | אין טבלאות ייחוס נפרדות |
| TAB_Attribute | רשימה סגורה בקוד הפרסום | אין טבלת מאפיינים |
| TAB_MaintenanceItem, APP_Human | קודי מחירון ותיאורים | אין קטלוגי פריטים ומקצועות נפרדים מלאים |
| TAB_ProfitPercent | ProfitPercent ברכיב או בהסכם | אין טבלה נפרדת |
| APP_Placement, APP_Space | קלט בקשה ושדות נבחרים בתוצאה | אין טבלאות השמות ומסגרות מלאות |
| APP_TransactionHeader, APP_TransactionDetails | TransactionHeaders, TransactionDetails | אותו תפקיד עסקי, לא התאמה מלאה בשדות |
| ניהול ריצות ושחזור | CalculationRuns, Consents | מבנים ייעודיים שנוספו ל־POC |

אין להסיק ממספר הטבלאות שכל האפיון מומש. דוגמת הנתונים אינה יבוא מלא של טבלאות Word. נדרש מיפוי שדות ואילוצים, החלטה על שמות ומבנה ואישור עסקי לפני שילוב בנתוני הארגון.
