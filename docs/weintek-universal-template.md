# Universell slavskärm (självkonfigurerande mall)

Status 2026-11-11: makron och test är klara. Mallbygget i EasyBuilder återstår
och är ett engångsjobb.

## Idén

MX43 lämnar hela sin konfiguration över Modbus. Skärmen letar därför själv upp
vilka detektorer som finns och hämtar namn, gas, enhet, område, larmgränser och
decimaler från centralen. Samma `.cxob` fungerar på alla anläggningar:

1. Teknikern programmerar MX43 som vanligt.
2. Teknikern laddar in standardfilen i Weintek-servern.
3. När Modbus kopplas upp fyller skärmen i sig själv.

Programmeras centralen om senare slår ändringen igenom utan att skärmen rörs.
`.cfg` behövs bara för att testa mot simulatorn.

## Vad som är klart

| Fil | Innehåll |
|---|---|
| `weintek/universal/macros/20_MX43 Scan.ebm` | Hittar detektorerna och hämtar deras konfiguration |
| `weintek/universal/macros/21_MX43 Runtime.ebm` | Hämtar mätvärde och larm varje sekund |
| `weintek/universal/macros/*.txt` | Samma makron som läsbar källtext (redigera dessa) |
| `weintek/universal/universal-lw-address-tag-library.csv` | Tagglista för alla LW-adresser nedan, 32 platser |
| `tools/weintek_universal.py` | `build` skapar `.ebm` och CSV, `check` testkör makrologiken |

`python3 tools/weintek_universal.py check` tolkar makrokällan och kör den mot en
simulerad MX43. Alla kontroller går igenom: tomma platser som svarar med nollor
respektive Modbus-fel, tappad länk före och mitt i en skanning, omprogrammerad
central, manuell omskanning, fler än 32 detektorer samt jämförelse av ordning,
namn och skalning mot generatorns planer för Volvo och Emhart.

Testet bevisar logiken, inte att EasyBuilders kompilator godtar syntaxen. Det
visar sig vid importen (se Verifieringar).

## Så fungerar makrona

**MX43 Scan** (ID 20, vid start och var 5:e sekund)

1. Läser INFO-blocket `3x-2600` (CRC och sekundräknare). Inget svar betyder att
   länken är nere: `LW-3322` sätts till 0 och ingenting annat ändras.
2. Gör en full skanning vid start, när CRC ändrats, när `LW-3327` sätts till 1
   och var tionde minut. Den läser alla 264 konfigurationsadresser och räknar en
   plats som använd när den har både namn och mätområde.
3. Skriver resultatet först när INFO-blocket gått att läsa även efter
   skanningen, så en länk som faller mitt i aldrig tömmer skärmen.
4. Uppdaterar varje körning konfigurationen för de platser som redan hittats,
   så att på/av och ändrade larmgränser syns inom 5 sekunder.

**MX43 Runtime** (ID 21, vid start och varje sekund)

Läser mätvärde (`3x-2000+adress`) och larmord (`3x-2300+adress`) för varje
använd plats, räknar fram larmnivå och skriver allt till fasta LW-adresser.
Misslyckas en läsning behålls senaste värdet.

Detektorerna läggs i adressordning: linje 1 detektor 1 först, analoga kanaler
sist. Det är samma ordning som den cfg-baserade generatorn ger för de
anläggningar vi har.

## LW-karta

Plats `n` är 1–32 och börjar på `LW n×100`. Offset 0–67 är MX43:s
konfigurationsblock kopierat rakt av, vilket är samma layout som dagens mall.

| Offset | Ord | Innehåll |
|---:|---:|---|
| 0 | 16 | Detektornamn (Unicode) |
| 16 | 1 | Status, 1 = på |
| 17 | 20 | Gasnamn (Unicode) |
| 37 | 1 | Mätområde, råvärde |
| 38 | 1 | Antal decimaler |
| 39 | 5 | Enhet (Unicode) |
| 44 | 6 | Förkortat gasnamn (Unicode) |
| 51–53 | 3 | Larmgräns 1–3, råvärde |
| 70 | 1 | Mätvärde, råvärde |
| 71 | 1 | Larmbitar |
| 72 | 1 | Mätvärde, råvärde (kopia, som i dagens mall) |
| 73 | 1 | Skaldivisor (1, 10, 100 …) |
| 74 | 1 | Antal konfigurerade larmnivåer |
| 75 | 1 | Larmnivå: 0 normal, 1 gul, 2 orange, 3 röd |
| 76 | 1 | Plats använd, 1 = ja |
| 77 | 1 | MX43-adress (1–264) |
| 78 | 1 | Linje (0 = analog kanal) |
| 79 | 1 | Detektornummer på linjen eller analog kanal |
| 80 | 4 | Mätområde som text med rätt decimaler (ASCII, 8 tecken) |
| 84 | 4 | Larmgräns 1 som text |
| 88 | 4 | Larmgräns 2 som text |
| 92 | 4 | Larmgräns 3 som text |
| 96 | 4 | Mätvärde som text |

Röd larmnivå ges av högsta konfigurerade larmnivån samt fel, under- och
överskridet område. En detektor med bara larm 1 och 2 blir alltså röd på larm 2.

Gemensamma adresser:

| Adress | Innehåll |
|---:|---|
| `LW-3300` | Projekttitel, 16 ord (skrivs inte av makrona, se Öppna frågor) |
| `LW-3320` | Antal visade detektorer (0–32) |
| `LW-3321` | Skanning: 0 ej gjord, 1 pågår första gången, 2 klar |
| `LW-3322` | Kontakt med MX43, 1 = ja |
| `LW-3323` | Antal detektorer centralen har totalt |
| `LW-3324` | 1 = fler än 32 detektorer, alla visas inte |
| `LW-3327` | Skriv 1 för att tvinga omskanning |
| `LW-3328` | Räknare för genomförda skanningar (felsökning) |
| `LW-3330` | Högsta larmnivå just nu över alla detektorer |
| `LW-3400` | MX43-adress per plats, 32 ord (intern tabell) |
| `LW-3440` | Högsta larmnivå per plats, 32 ord (intern tabell) |
| `LW-3480` | Decimaler per plats, 32 ord (intern tabell) |
| `LW-4000` | Mätvärde som 32-bitars flyttal i rätt enhet, 32 st (64 ord) |
| `LW-4100` | Mätvärde som råvärde, 32 ord |
| `LW-4200` | Larmbitar, 32 ord |
| `LW-4300` | Larmnivå 0–3, 32 ord |

Blocken från `LW-4000` är till för Data Sampling och trender, som kräver
sammanhängande adresser. Oanvända platser är alltid 0.

## Arbetslista i EasyBuilder (engångsjobb)

Utgå från den nyaste mallen med 32 platser (`Start`-familjen) och spara den
som ett nytt projekt.

1. **Makron.** Ta bort Info Extractor-makrona (ID 5, 7, 8, 9) och, om de finns,
   Runtime Sampler (10) och Project Initializer (11). Importera `20_MX43 Scan.ebm`
   och `21_MX43 Runtime.ebm`. Båda ska hamna under "Compilation succeeded".
2. **Taggar.** Importera `universal-lw-address-tag-library.csv` och ersätt taggar
   med samma namn. `info-Dn`, `meas-Dn` och `alarm-Dn` mot MX43 behövs inte längre.
3. **Detektorrutor, alla 32.** Namn, gas, enhet och status ligger kvar på samma
   adresser som förut. Ändra:
   - Mätvärde: ASCII-objekt på `DetN-ValueText` (4 ord). Då blir decimalerna rätt
     oavsett gas.
   - Mätområde och larmgränser: ASCII-objekt på `DetN-RangeText` och
     `DetN-Alarm1Text`–`Alarm3Text`.
   - Färg: styrs av `DetN-AlarmSeverity` (0–3) i stället för larmbitarna.
   - Synlighet: rutan visas bara när `DetN-Used` är 1.
4. **Data Sampling.** Ta bort definitionerna som läser från MX43. Skapa en som
   läser 32 flyttal från `LW-4000` varje sekund. Kanal `n` är plats `n`.
5. **Trender.** Bind trendobjekten till den nya samplingen. Y-axeln kan ta sitt
   max från `DetN-Range` delat med `DetN-ScaleDivisor`.
6. **Statusrad.** Visa "Ingen kontakt med MX43" när `MX43-LinkOk` är 0,
   "Söker detektorer" när `MX43-ScanState` är 1 och en varning när
   `MX43-Overflow` är 1. En knapp som sätter `MX43-RescanRequest` till 1 är bra
   att ha på en serviceskärm.
7. **Enheten MX43.** Stäng av dialogrutan "PLC no response" för enheten. Tomma
   platser kan annars ge popup-rutor under skanningen.
8. **Kompilera** till `.cxob` och kör acceptanstestet nedan.

## Verifieringar som måste göras

1. **Kompilering.** Makrona använder variabel som adress i `GetDataEx` och
   `SetData`. Hjälpfilen visar inget exempel på det, men kompilatorn har ett
   meddelande om "Address variables … in Macro functions (i.e. SetData,
   GetData)", så det bör vara tillåtet. Importen i steg 1 avgör saken.
2. **Tomma platser på en riktig MX43.** Makrot klarar både nollor och Modbus-fel.
   Det som måste kontrolleras är att en oanvänd plats inte svarar med ett
   standardnamn och ett mätområde, för då räknas den som använd.
3. **CRC.** Kontrollera att registret `2600`–`2601` ändras när centralen
   programmeras om. Gör det inte det tar ändringen upp till tio minuter, eller
   kräver omskanningsknappen.
4. **Skanningstid.** En full skanning är 264 läsningar plus en per använd plats.
   Mät tiden mot en riktig central. Simulatorn sätter i dag CRC till 0 och svarar
   omedelbart, så den visar varken punkt 3 eller 4.

## Acceptanstest

Kör mot `Mx43Sim` med minst en digital konfiguration (Volvo), en analog (Emhart)
och en med fler än 19 detektorer.

- Alla detektorer i `.cfg` visas i rätt ordning med namn, gas, enhet, område och
  larmgränser. Övriga rutor är dolda.
- O2 visas med en decimal och CO2 i %VOL med två.
- Ändrat mätvärde i simulatorn syns inom en sekund och färgen följer larmnivån.
- Av/på i simulatorn syns inom fem sekunder.
- Stoppad simulator ger "Ingen kontakt" och värdena står kvar. Startad igen
  återgår skärmen utan omstart.
- Byte till en annan `.cfg` följt av omskanning ger den nya anläggningen.
- Trenden ritar rätt kanal och historiken hamnar på USB.

## Öppna frågor

- **MX43:ans IP-adress.** Den är inställd i projektet. Antingen har alla
  anläggningar samma adress på slavnätet, eller så behövs en inställningssida på
  skärmen där teknikern anger den.
- **Projekttitel.** Centralens projektnamn finns inte i registerkartan. Välj
  mellan generisk rubrik, ett inmatningsfält på serviceskärmen eller att verktyget
  patchar in titeln i den färdiga filen.
- **Fler än 32 detektorer.** Mallen visar de 32 första. Större anläggningar kräver
  en mall med fler platser; makrona skalar om 32 byts mot ett högre tal.
- **Historikens mappnamn** blir samma på alla anläggningar eftersom filen är
  densamma.
- **Ändrad detektorordning.** Läggs en detektor till mitt i adressordningen
  flyttas de efterföljande en plats, även i historiken.

## Bakgrund: varför inte patcha filen direkt

Makrona kompileras till bytekod i `project`, och taggadresserna bakas in i
makrots konstanttabell vid kompileringen. Dagens `.cxob`-patch ändrar bara
taggtabellen, så en patchad fil som laddas utan omkompilering läser fortfarande
mallens adresser. Den universella mallen kringgår det genom att inga adresser är
anläggningsspecifika.
