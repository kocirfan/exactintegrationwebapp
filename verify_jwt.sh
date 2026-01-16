#!/bin/bash

# Base URL
BASE_URL="http://localhost:5057/api"

# 1. Register
echo "Registering user..."
REGISTER_RESPONSE=$(curl -s -X POST "$BASE_URL/auth/register" \
  -H "Content-Type: application/json" \
  -d '{
    "username": "verifyuser",
    "email": "verify@example.com",
    "password": "Password123!"
  }')
echo "Register Response: $REGISTER_RESPONSE"

# 2. Login
echo "Logging in..."
LOGIN_RESPONSE=$(curl -s -X POST "$BASE_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d '{
    "username": "verifyuser",
    "password": "Password123!"
  }')
echo "Login Response: $LOGIN_RESPONSE"

# Extract Token (simple grep/sed, assuming json response)
TOKEN=$(echo $LOGIN_RESPONSE | grep -o '"token":"[^"]*' | cut -d'"' -f4)

if [ -z "$TOKEN" ]; then
  echo "Failed to get token"
  exit 1
fi

echo "Token: $TOKEN"

# 3. Access Protected Endpoint (Report)
echo "Accessing Protected Endpoint..."
PROTECTED_RESPONSE=$(curl -s -X GET "$BASE_URL/report/top-sales-by-date?startDate=2024-01-01&endDate=2024-01-31&topCount=5" \
  -H "Authorization: Bearer $TOKEN")

# Check if response contains "401" or "Unauthorized" (basic check)
if [[ "$PROTECTED_RESPONSE" == *"Unauthorized"* ]] || [[ "$PROTECTED_RESPONSE" == *"401"* ]]; then
    echo "Access Denied (Unexpected)"
else
    echo "Access Granted (Success)"
    # echo "Response: $PROTECTED_RESPONSE" # Might be large
fi

# 4. Access Without Token
echo "Accessing Without Token..."
UNAUTH_RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X GET "$BASE_URL/report/top-sales-by-date?startDate=2024-01-01&endDate=2024-01-31&topCount=5")
echo "Status Code Without Token: $UNAUTH_RESPONSE"
