# Pull the PostgreSQL Docker Image
docker pull postgres

# Run the PostgreSQL Container
docker run --name postgres_dev --rm -e POSTGRES_USER=akka -e POSTGRES_PASSWORD=akka -e POSTGRES_DB=akka -d -p 5432:5432 postgres

Write-Output "PostgreSQL setup is complete."