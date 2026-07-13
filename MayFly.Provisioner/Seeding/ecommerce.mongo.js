// E-commerce seed for MongoDB — runs as admin in /docker-entrypoint-initdb.d/.
// Target appdb explicitly (default init db is not guaranteed).
var appdb = db.getSiblingDB("appdb");

appdb.getCollection("products").insertMany([
  { _id: 1,  name: "Wireless Headphones",  price: 79.99, category: "Electronics"  },
  { _id: 2,  name: "Running Shoes",        price: 59.99, category: "Footwear"      },
  { _id: 3,  name: "Coffee Maker",         price: 49.95, category: "Appliances"   },
  { _id: 4,  name: "Yoga Mat",             price: 29.99, category: "Fitness"       },
  { _id: 5,  name: "Bluetooth Speaker",    price: 39.99, category: "Electronics"  },
  { _id: 6,  name: "Water Bottle",         price: 19.99, category: "Fitness"       },
  { _id: 7,  name: "Desk Lamp",            price: 24.99, category: "Home Office"  },
  { _id: 8,  name: "Mechanical Keyboard",  price: 89.99, category: "Electronics"  },
  { _id: 9,  name: "Notebook Set",         price: 14.99, category: "Stationery"   },
  { _id: 10, name: "Sunglasses",           price: 34.99, category: "Accessories"  }
]);

appdb.getCollection("customers").insertMany([
  { _id: 1, name: "Alice Martin",   email: "alice@example.com"  },
  { _id: 2, name: "Bob Johnson",    email: "bob@example.com"    },
  { _id: 3, name: "Carol Williams", email: "carol@example.com"  },
  { _id: 4, name: "David Brown",    email: "david@example.com"  },
  { _id: 5, name: "Eva Garcia",     email: "eva@example.com"    }
]);

appdb.getCollection("orders").insertMany([
  { _id: 2001, customer_id: 1, order_date: "2026-01-05", total: 109.98,
    items: [ { product_id: 1, quantity: 1, unit_price: 79.99 },
             { product_id: 4, quantity: 1, unit_price: 29.99 } ] },
  { _id: 2002, customer_id: 2, order_date: "2026-01-08", total: 59.99,
    items: [ { product_id: 2, quantity: 1, unit_price: 59.99 } ] },
  { _id: 2003, customer_id: 3, order_date: "2026-01-11", total: 64.94,
    items: [ { product_id: 3, quantity: 1, unit_price: 49.95 },
             { product_id: 9, quantity: 1, unit_price: 14.99 } ] },
  { _id: 2004, customer_id: 4, order_date: "2026-01-15", total: 89.99,
    items: [ { product_id: 8, quantity: 1, unit_price: 89.99 } ] },
  { _id: 2005, customer_id: 5, order_date: "2026-01-20", total: 69.98,
    items: [ { product_id: 4, quantity: 1, unit_price: 29.99 },
             { product_id: 5, quantity: 1, unit_price: 39.99 } ] },
  { _id: 2006, customer_id: 1, order_date: "2026-02-02", total: 39.99,
    items: [ { product_id: 5, quantity: 1, unit_price: 39.99 } ] },
  { _id: 2007, customer_id: 2, order_date: "2026-02-10", total: 44.98,
    items: [ { product_id: 6, quantity: 1, unit_price: 19.99 },
             { product_id: 7, quantity: 1, unit_price: 24.99 } ] },
  { _id: 2008, customer_id: 3, order_date: "2026-02-14", total: 24.99,
    items: [ { product_id: 7, quantity: 1, unit_price: 24.99 } ] }
]);
