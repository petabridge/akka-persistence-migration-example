# PowerShell script to set up a local MongoDB development environment using Docker

# Step 1: Pull the MongoDB Docker Image
docker pull mongo

# Step 2: Run the MongoDB Container with Replica Set Configuration
docker run --name mongo-dev -d --rm -p 27017:27017 -e MONGO_REPLICA_SET_NAME=rs0 mongo --replSet rs0 --bind_ip_all --noauth

# Wait for a few seconds to ensure MongoDB is fully started
Start-Sleep -Seconds 5

# Step 3: Initiate the Replica Set
docker exec -it mongo-dev mongosh --eval "rs.initiate()"

Write-Host "MongoDB is set up as a single-node replica set without authentication and is ready for use."
