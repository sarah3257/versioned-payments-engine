# API מקומי

בסיס: `http://127.0.0.1:5088`. בקשות JSON עם `Content-Type: application/json`. Enum נשלח בשמו, למשל Maintenance או Public.

| HTTP | נתיב | פעולה |
|---|---|---|
| GET | /api/health | מצב השרת וגבולות החישוב |
| GET | /api/catalog | קטלוג גרסאות והפניות |
| GET | /api/schema | סכמה אמיתית מה־EF model |
| POST | /api/components/publish | פרסום גרסת רכיב מלאה |
| POST | /api/models/publish | פרסום הרכב מודל ושכבות |
| POST | /api/consents | הוספת הסכמת מסגרת |
| POST | /api/prices/publish | מחירון בגרסה חדשה |
| POST | /api/agreements/publish | הסכם רווח למסגרת |
| POST | /api/tax-rates/publish | שיעור מס |
| POST | /api/calculations | חישוב ושמירה בסיום |
| POST | /api/simulations | חישוב ללא כתיבה; SimulationModelVersion יכול לעקוף הסכמה לצורך השוואה |
| GET | /api/runs | 100 ריצות אחרונות |
| GET | /api/runs/{id} | תוצאה מקורית |
| GET | /api/runs/{id}/snapshot | תוכנית מקורית מלאה לשחזור |
| POST | /api/runs/{id}/replay | שחזור והשוואה, ללא תשלום נוסף |

חוזי הבקשות המדויקים מוגדרים ב־Domain/Contracts.cs. Attributes בטבלת החלטה: institutionType, supportLevel, institution, ownership, administration, population. אין הוספת מאפיין חדש דרך נתון בלבד; נדרש להרחיב את חוזה האירוע והמיפוי.

בקשה לדוגמה:

```json
{
  "requestKey": "placement-500-sep-initial",
  "modelId": 1,
  "institution": 3404,
  "placementId": "500",
  "eventDate": "2026-09-15",
  "ownership": "Private",
  "institutionType": "דירה",
  "supportLevel": 4,
  "administration": "מוגבלויות",
  "population": "",
  "monthlyFactor": 1
}
```

פלט: RunId, Header ובו Details, Trace וסימון Simulation. סכומים נשמרים ב־decimal. שגיאות עסקיות: 422; RequestKey עם תוכן שונה או התנגשות פרסום: 409; לא נמצא: 404.

אין endpoint שמקבל SQL חופשי ואין ביטויי קוד חופשיים בטבלת ההחלטה. אין endpoint כשל־בדיקה בשרת: fault injection נמצא רק בשירות ובבדיקות האוטומטיות.
