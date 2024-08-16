# Pull the PostgreSQL Docker Image
docker pull postgres

# Run the PostgreSQL Container
docker run --name postgres_dev --rm -e POSTGRES_PASSWORD=mysecretpassword -d -p 5432:5432 postgres

# Wait for a few seconds to ensure PostgreSQL is fully started
Start-Sleep -Seconds 5

# Create a Database
docker exec -it postgres_dev psql -U postgres -c "CREATE DATABASE akka;"

Write-Output "PostgreSQL setup is complete."