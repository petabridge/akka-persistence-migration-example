# Akka.Persistence Migration Example

An example of how to migrate between Akka.Persistence implementations in Akka.NET using actors, Akka.NET serialization, and persistence configuration.

In this example, we will be migrating existing MongoDB database to PostgreSQL using the migration app.

# Technologies Used

* [Akka.Persistence.MongoDb](https://github.com/akkadotnet/Akka.Persistence.MongoDB/) as the source persistence database that will be migrated
* [Akka.Persistence.MongoDb.Hosting](https://github.com/akkadotnet/Akka.Persistence.MongoDB/tree/dev/src/Akka.Persistence.MongoDb.Hosting) 
* [Akka.Persistence.Sql](https://github.com/akkadotnet/akka.persistence.sql) targeting PostgreSQL database as the target database to migrate into
* [Akka.Persistence.Sql.Hosting](https://github.com/akkadotnet/akka.persistence.sql)
* [MongoDb](https://www.mongodb.com/)
* [NpgSql](https://www.npgsql.org/)

# Running The Example

To run this example, you will need to have docker installed on your computer.

## Starting The Infrastructure

### Database Docker Containers

There are 2 Powershell script provided to start a MongoDB and a PostgreSQL docker containers, we will need to start them before we start the application.

```powershell
.\start-mongo.ps1
```

```powershell
.\start-postgres.ps1
```

### Seeding MongoDB

To seed the MongoDB database with data, you can either run the `App.MongoDb SEED` launch configuration from your IDE, or use this CLI command:

```powershell
dotnet run --project .\src\App.MongoDb\App.MongoDb.csproj -- seed
```

The seeding app will persist 11,000 messages across 50 actors evenly (220 messages per actor), creating a snapshot every 50 messages.  You should see the actors reporting its final state of 200 data set like so:

```text
Persistence ID Count: 50

╔════════════════════╤════════════════╤════════╗
║Persistence Id      │Last Sequence Nr│Checksum║
╠════════════════════╪════════════════╪════════╣
║persistent-worker-00│220             │1204500 ║
║persistent-worker-01│220             │1204720 ║
║persistent-worker-02│220             │1204940 ║
║persistent-worker-03│220             │1205160 ║
║persistent-worker-04│220             │1205380 ║
║persistent-worker-05│220             │1205600 ║
║persistent-worker-06│220             │1205820 ║
║persistent-worker-07│220             │1206040 ║
║persistent-worker-08│220             │1206260 ║
║persistent-worker-09│220             │1206480 ║
║persistent-worker-10│220             │1206700 ║
║persistent-worker-11│220             │1206920 ║
║persistent-worker-12│220             │1207140 ║
║persistent-worker-13│220             │1207360 ║
║persistent-worker-14│220             │1207580 ║
║persistent-worker-15│220             │1207800 ║
║persistent-worker-16│220             │1208020 ║
║persistent-worker-17│220             │1208240 ║
║persistent-worker-18│220             │1208460 ║
║persistent-worker-19│220             │1208680 ║
║persistent-worker-20│220             │1208900 ║
║persistent-worker-21│220             │1209120 ║
║persistent-worker-22│220             │1209340 ║
║persistent-worker-23│220             │1209560 ║
║persistent-worker-24│220             │1209780 ║
║persistent-worker-25│220             │1210000 ║
║persistent-worker-26│220             │1210220 ║
║persistent-worker-27│220             │1210440 ║
║persistent-worker-28│220             │1210660 ║
║persistent-worker-29│220             │1210880 ║
║persistent-worker-30│220             │1211100 ║
║persistent-worker-31│220             │1211320 ║
║persistent-worker-32│220             │1211540 ║
║persistent-worker-33│220             │1211760 ║
║persistent-worker-34│220             │1211980 ║
║persistent-worker-35│220             │1212200 ║
║persistent-worker-36│220             │1212420 ║
║persistent-worker-37│220             │1212640 ║
║persistent-worker-38│220             │1212860 ║
║persistent-worker-39│220             │1213080 ║
║persistent-worker-40│220             │1213300 ║
║persistent-worker-41│220             │1213520 ║
║persistent-worker-42│220             │1213740 ║
║persistent-worker-43│220             │1213960 ║
║persistent-worker-44│220             │1214180 ║
║persistent-worker-45│220             │1214400 ║
║persistent-worker-46│220             │1214620 ║
║persistent-worker-47│220             │1214840 ║
║persistent-worker-48│220             │1215060 ║
║persistent-worker-49│220             │1215280 ║
╚════════════════════╧════════════════╧════════╝
```

Note that each actor received 20 recovery event and stored 220 data in its state.

### Migrating The Database

To start the database migration, run the `Akka.Persistence.Migration` app either using your IDE of through the CLI command:

```powershell
dotnet run --project .\src\Akka.Persistence.Migration\Akka.Persistence.Migration.csproj
```

### Validating The Migration Process

To validate that migration has been completed successfully, we will use the `App.PostgreSql` app. This app is practically identical to `App.MongoDb`, with the main difference that we swapped out `Akka.Persistence.MongoDb` with `Akka.Persistence.Sql` and it does not have the seeding code.

Run the `App.PostgreSql` app in your IDE or use the CLI command:

```powershell
dotnet run --project .\src\App.PostgreSql\App.PostgreSql.csproj
```

All the data should be migrated over to PostgreSQL and you should see an identical output to `App.MongoDb`

```text
Persistence ID Count: 50

╔════════════════════╤════════════════╤════════╗
║Persistence Id      │Last Sequence Nr│Checksum║
╠════════════════════╪════════════════╪════════╣
║persistent-worker-00│220             │1204500 ║
║persistent-worker-01│220             │1204720 ║
║persistent-worker-02│220             │1204940 ║
║persistent-worker-03│220             │1205160 ║
║persistent-worker-04│220             │1205380 ║
║persistent-worker-05│220             │1205600 ║
║persistent-worker-06│220             │1205820 ║
║persistent-worker-07│220             │1206040 ║
║persistent-worker-08│220             │1206260 ║
║persistent-worker-09│220             │1206480 ║
║persistent-worker-10│220             │1206700 ║
║persistent-worker-11│220             │1206920 ║
║persistent-worker-12│220             │1207140 ║
║persistent-worker-13│220             │1207360 ║
║persistent-worker-14│220             │1207580 ║
║persistent-worker-15│220             │1207800 ║
║persistent-worker-16│220             │1208020 ║
║persistent-worker-17│220             │1208240 ║
║persistent-worker-18│220             │1208460 ║
║persistent-worker-19│220             │1208680 ║
║persistent-worker-20│220             │1208900 ║
║persistent-worker-21│220             │1209120 ║
║persistent-worker-22│220             │1209340 ║
║persistent-worker-23│220             │1209560 ║
║persistent-worker-24│220             │1209780 ║
║persistent-worker-25│220             │1210000 ║
║persistent-worker-26│220             │1210220 ║
║persistent-worker-27│220             │1210440 ║
║persistent-worker-28│220             │1210660 ║
║persistent-worker-29│220             │1210880 ║
║persistent-worker-30│220             │1211100 ║
║persistent-worker-31│220             │1211320 ║
║persistent-worker-32│220             │1211540 ║
║persistent-worker-33│220             │1211760 ║
║persistent-worker-34│220             │1211980 ║
║persistent-worker-35│220             │1212200 ║
║persistent-worker-36│220             │1212420 ║
║persistent-worker-37│220             │1212640 ║
║persistent-worker-38│220             │1212860 ║
║persistent-worker-39│220             │1213080 ║
║persistent-worker-40│220             │1213300 ║
║persistent-worker-41│220             │1213520 ║
║persistent-worker-42│220             │1213740 ║
║persistent-worker-43│220             │1213960 ║
║persistent-worker-44│220             │1214180 ║
║persistent-worker-45│220             │1214400 ║
║persistent-worker-46│220             │1214620 ║
║persistent-worker-47│220             │1214840 ║
║persistent-worker-48│220             │1215060 ║
║persistent-worker-49│220             │1215280 ║
╚════════════════════╧════════════════╧════════╝
```