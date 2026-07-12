// Northwind seed for MongoDB — runs as admin in /docker-entrypoint-initdb.d/.
// Target appdb explicitly (default init db is not guaranteed).
var appdb = db.getSiblingDB("appdb");

appdb.getCollection("products").insertMany([
  { _id: 1,  name: "Chai",                              price: 18.00 },
  { _id: 2,  name: "Chang",                             price: 19.00 },
  { _id: 3,  name: "Aniseed Syrup",                     price: 10.00 },
  { _id: 4,  name: "Chef Anton's Cajun Seasoning",      price: 22.00 },
  { _id: 5,  name: "Grandma's Boysenberry Spread",      price: 25.00 },
  { _id: 6,  name: "Uncle Bob's Organic Dried Pears",   price: 30.00 },
  { _id: 7,  name: "Northwoods Cranberry Sauce",        price: 40.00 },
  { _id: 8,  name: "Mishi Kobe Niku",                   price: 97.00 },
  { _id: 9,  name: "Ikura",                             price: 31.00 },
  { _id: 10, name: "Queso Cabrales",                    price: 21.00 },
  { _id: 11, name: "Queso Manchego La Pastora",         price: 38.00 },
  { _id: 12, name: "Konbu",                             price:  6.00 },
  { _id: 13, name: "Tofu",                              price: 23.25 },
  { _id: 14, name: "Genen Shouyu",                      price: 15.50 },
  { _id: 15, name: "Pavlova",                           price: 17.45 }
]);

appdb.getCollection("customers").insertMany([
  { _id: 1,  name: "Alfreds Futterkiste",           city: "Berlin"     },
  { _id: 2,  name: "Around the Horn",               city: "London"     },
  { _id: 3,  name: "Bottom-Dollar Markets",         city: "Vancouver"  },
  { _id: 4,  name: "Berglunds snabbkop",            city: "Lulea"      },
  { _id: 5,  name: "Blondel pere et fils",          city: "Strasbourg" },
  { _id: 6,  name: "Bolido Comidas preparadas",     city: "Madrid"     },
  { _id: 7,  name: "Bon app",                       city: "Marseille"  },
  { _id: 8,  name: "Ernst Handel",                  city: "Graz"       },
  { _id: 9,  name: "Familia Arquibaldo",            city: "Sao Paulo"  },
  { _id: 10, name: "FISSA Fabrica Inter. Salchichas", city: "Madrid"   }
]);

appdb.getCollection("orders").insertMany([
  { _id: 1001, customer_id: 1,  order_date: "2026-01-10" },
  { _id: 1002, customer_id: 2,  order_date: "2026-01-12" },
  { _id: 1003, customer_id: 3,  order_date: "2026-01-15" },
  { _id: 1004, customer_id: 4,  order_date: "2026-01-20" },
  { _id: 1005, customer_id: 5,  order_date: "2026-01-22" },
  { _id: 1006, customer_id: 6,  order_date: "2026-02-01" },
  { _id: 1007, customer_id: 7,  order_date: "2026-02-03" },
  { _id: 1008, customer_id: 8,  order_date: "2026-02-05" },
  { _id: 1009, customer_id: 9,  order_date: "2026-02-10" },
  { _id: 1010, customer_id: 10, order_date: "2026-02-14" },
  { _id: 1011, customer_id: 1,  order_date: "2026-02-18" },
  { _id: 1012, customer_id: 2,  order_date: "2026-02-20" }
]);

appdb.getCollection("order_details").insertMany([
  { order_id: 1001, product_id: 1,  quantity: 12 },
  { order_id: 1001, product_id: 2,  quantity: 10 },
  { order_id: 1001, product_id: 3,  quantity:  5 },
  { order_id: 1002, product_id: 4,  quantity:  8 },
  { order_id: 1002, product_id: 5,  quantity:  6 },
  { order_id: 1002, product_id: 6,  quantity:  4 },
  { order_id: 1003, product_id: 7,  quantity:  3 },
  { order_id: 1003, product_id: 8,  quantity:  2 },
  { order_id: 1003, product_id: 9,  quantity:  7 },
  { order_id: 1004, product_id: 10, quantity:  9 },
  { order_id: 1004, product_id: 11, quantity:  5 },
  { order_id: 1004, product_id: 12, quantity: 20 },
  { order_id: 1005, product_id: 13, quantity:  4 },
  { order_id: 1005, product_id: 14, quantity:  6 },
  { order_id: 1005, product_id: 15, quantity:  8 },
  { order_id: 1006, product_id: 1,  quantity: 15 },
  { order_id: 1006, product_id: 3,  quantity: 10 },
  { order_id: 1007, product_id: 4,  quantity:  5 },
  { order_id: 1007, product_id: 5,  quantity:  3 },
  { order_id: 1007, product_id: 6,  quantity:  2 },
  { order_id: 1008, product_id: 7,  quantity:  6 },
  { order_id: 1008, product_id: 8,  quantity:  1 },
  { order_id: 1008, product_id: 9,  quantity: 10 },
  { order_id: 1009, product_id: 10, quantity:  4 },
  { order_id: 1009, product_id: 11, quantity:  7 },
  { order_id: 1010, product_id: 12, quantity: 30 },
  { order_id: 1010, product_id: 13, quantity:  2 },
  { order_id: 1011, product_id: 14, quantity:  5 },
  { order_id: 1011, product_id: 15, quantity:  3 },
  { order_id: 1012, product_id: 1,  quantity:  8 }
]);
