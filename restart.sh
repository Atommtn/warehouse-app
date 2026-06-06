#!/bin/bash
echo "=== Stopping old container ==="
docker-compose down

echo ""
echo "=== Building fresh image ==="
docker-compose build --no-cache

echo ""
echo "=== Starting container ==="
docker-compose up -d

echo ""
echo "=== Waiting 5 seconds for startup ==="
sleep 5

echo ""
echo "=== Container status ==="
docker ps | grep warehouse

echo ""
echo "=== Container logs ==="
docker logs warehouse-app --tail 30

echo ""
echo "=== Testing local connection ==="
curl -s -o /dev/null -w "HTTP Status: %{http_code}\n" http://localhost:8080 || echo "curl not available"

echo ""
echo "=== Port bindings ==="
docker port warehouse-app
