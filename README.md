
# Sport Events Processor - Codium zadanie

Robustná konzolová aplikácia na spracovanie športových udalostí, s podporou paralelného spracovania a bezpečným ukladaním do SQL Server databázy.

Projekt spúšťam príkazom:
`dotnet run --project EventProcessor`

---

## 🔧 Setup

### 1. Nastavenie connection stringu

Connection string do DB môže byť **definovaný ako premenná prostredia** `DB_CONNECTION_STRING` alebo v súbore `appsettings.json`.

### 2. Databáza – SQL Server

Projekt používa SQL Server, ako bolo uvedené v zadaní.
*   SQL Server vieme spustiť cez `docker-compose` (súčasťou repozitára).
*   Alternatívne som zvažoval `SQLite` pre jednoduchší setup
*   Tabuľky `Events` a `Odds` sa **automaticky vytvoria pri spustení aplikácie**, ak ešte neexistujú.

### 3. Maximálny počet paralelných databázových pripojení

V aplikácii je použitý `Semaphore`, vďaka čomu nikdy neprekročíme definovaný počet aktívnych DB pripojení. **Hodnota je konfigurovateľná** v kóde.

### 4. .NET Runtime

Projekt bol vytvorený pre štandardnú verziu **.NET 9**. Ak ju však nemáte nainštalovanú, pripravil som aj **self-contained .exe**, ktorý obsahuje **zabudovaný runtime**, takže ho môžete jednoducho spustiť bez dodatočnej inštalácie.

---

## ⛁ Databáza

### 1. Dávkové spracovanie (Batching)

Namiesto toho, aby sa pre každý `Odd` volal samostatný databázový príkaz, aplikácia vytvára jeden väčší SQL príkaz.

Ten sa spustí až vtedy, keď zahrnieme všetky `Odd` alebo počet parametrov dosiahne maximum parametrov pre `SQL Server`

### 2. Ochrana proti SQL injekcii

Všetky databázové príkazy používajú **parametrizované SQL dotazy**, čím je zaistená ochrana proti útokom typu SQL injection.

### 3. Retry mechanizmus

Pri probléme s ukladaním dát sa aplikuje mechanizmus opakovania. Exponenciálne zvyšujeme čas medzi pokusmi.

### 4. Ukladanie jedného eventu prebieha v transakcii

Ak sa pri ukladaní `Odds` stane chyba, vďaka transakcii sa neuloží nič. Tým sa **zabráni nekonzistentným dátam** a polovičnému uloženiu eventu.

---

## Spracovanie eventov

V zdrojovom súbore sa jeden event aj jeden odds môže objaviť viackrát, ale v databáze musí existovať iba jeden unikátny záznam.
*   Posledná verzia eventu prepíše staršiu (napr. zmena dátumu).
*   Posledná verzia odds prepíše staršiu (zmena statusu, kurzu).
*   **Poradie odds je zachované** presne tak, ako bolo v zdrojovom JSON.

**Zapracovanie týchto úprav prebieha v pamäti,** čo je rýchlejšie ako prepisovanie eventov v rámci databázy.

#### Zabezpečenie konzistencie dát pri paralelnom spracovaní

Ak by sa posielali dva paralelné DB dotazy s rovnakým eventom, vzniká *race condition*:
1.  `Query A` načíta a prepíše event.
2.  `Query B`, ktoré ešte pracuje so starými dátami, prepíše event späť.

Preto sa jeden konkrétny event ukladá vždy v rámci jednej transakcie a iba v jednej paralelnej jednotke.
