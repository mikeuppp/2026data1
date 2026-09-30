import ijson
import csv

input_file = "data_20260127.json"     # change to your file name
output_file = "businesses2.csv"

k = 0

def clean(value):
    """Return cleaned string or None if value is None, empty, or 'None'."""
    if value is None:
        return None
    value = str(value).strip()
    if not value or value.lower() == "none":
        return None
    return value

def format_address(addr, city):
    if not addr:
        return None

    street = clean(addr.get("street"))
    building = clean(addr.get("buildingNumber"))
    entrance = clean(addr.get("entrance"))
    post_code = clean(addr.get("postCode"))

    parts = []
    if street:
        parts.append(street.title())
    if building:
        parts.append(building)
    if entrance:
        parts.append(entrance)

    street_part = " ".join(parts)

    if post_code:
        street_line = f"{street_part}, {post_code}" if street_part else post_code
    else:
        street_line = street_part

    if city:
        return f"{street_line} {city}" if street_line else city
    return street_line


with open(input_file, "r", encoding="utf-8") as f, \
     open(output_file, "w", newline="", encoding="utf-8-sig") as out:   


    writer = csv.writer(out)
    writer.writerow([
        "business_id",
        "name",
        "business_type",
        "address",
        "city",
        "listing_type"
    ])

    for company in ijson.items(f, "item"):
        # industry code
        main_line = company.get("mainBusinessLine")
        if not main_line:
            continue

        industry_code = main_line.get("type")
        # if industry_code is None or int(industry_code) != 69201:
        if industry_code is None or int(industry_code) != 95312:
            continue

        # business type — ONLY Finnish (languageCode == "1"), else skip
        forms = company.get("companyForms", [])
        forms1 = forms[0].get("descriptions", [])
        business_type = next(
            (d for d in forms1 if d.get("languageCode") == "1"),
            None
        )
        business_type = business_type.get("description")
        if not business_type:
            continue  # skip company if no Finnish description

        # business_id
        business_id = company.get("businessId", {}).get("value")

        # name
        names = company.get("names", [])
        name = names[0].get("name") if names else None

        # address & city
        addresses = company.get("addresses", [])
        city = None
        address = None
        if addresses:
            post_offices = addresses[0].get("postOffices", [])
            if post_offices:
                cityData = next(
                    (d for d in post_offices if d.get("languageCode") == "1"),
                    None
                )
                city = cityData.get("city").title()
            address = format_address(addresses[0], city)

        # listing_type
        registered_entries = company.get("registeredEntries", [])
        # listing_type = registered_entries[0].get("type") if registered_entries else None
        listing_type = "ilmainen"

        writer.writerow([
            business_id,
            name,
            business_type,
            address,
            city,
            listing_type
        ])

        k += 1
        print(k)
        # if k > 10:
        #     break

print(f"✅ CSV file created: {output_file}")
